using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Expose a bounded compositor focus query through the desktop connection.</summary>
internal sealed class DesktopFocus(DesktopBus bus,
    Func<int, FocusProcesses.Candidate[], CancellationToken, Task<string>>? match = null) : IPathMethodHandler
{
    internal const string Service = "org.openxlr.Desktop";
    public string Path => "/org/openxlr/Desktop";
    public bool HandlesChildPaths => false;
    private readonly SemaphoreSlim _oneRequest = new(1);
    private readonly Func<int, FocusProcesses.Candidate[], CancellationToken, Task<string>> _match = match ?? FocusProcesses.MatchKeysAsync;
    private volatile string? _cookie, _kwinOwner;
    private volatile TaskCompletionSource<uint>? _answer;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml(["""
                <interface name="org.openxlr.Desktop">
                  <method name="GetFocusedAudioProcesses"><arg type="s" direction="in"/><arg type="s" direction="out"/></method>
                  <method name="ReportFocus"><arg type="s" direction="in"/><arg type="s" direction="in"/></method>
                </interface>
                """u8.ToArray()]);
        }
        else if (context.Request.InterfaceAsString == Service && context.Request.MemberAsString == "GetFocusedAudioProcesses"
            && context.Request.SignatureAsString == "s")
        {
            var reader = context.Request.GetBodyReader();
            var bytes = reader.ReadStringAsSpan();
            if (bytes.Length > FocusProcesses.MaxCandidateTextBytes)
            {
                context.ReplyError("org.openxlr.Desktop.Unavailable", "Too many focus candidates.");
                return ValueTask.CompletedTask;
            }
            string candidates = Encoding.UTF8.GetString(bytes);
            context.DisposesAsynchronously = true;
            _ = ReplyFocusAsync(context, candidates);
        }
        else if (context.Request.InterfaceAsString == Service && context.Request.MemberAsString == "ReportFocus"
            && context.Request.SignatureAsString == "ss")
        {
            var reader = context.Request.GetBodyReader();
            string cookie = reader.ReadString(), process = reader.ReadString();
            if (context.Request.SenderAsString == _kwinOwner && cookie == _cookie
                && uint.TryParse(process, NumberStyles.None, CultureInfo.InvariantCulture, out uint pid))
                _answer?.TrySetResult(pid);
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
        }
        else context.ReplyUnknownMethodError();
        return ValueTask.CompletedTask;
    }

    private async Task ReplyFocusAsync(MethodContext context, string candidates)
    {
        bool entered = false;
        try
        {
            if (!await _oneRequest.WaitAsync(0, context.RequestAborted))
                throw new InvalidOperationException("a focus query is already running");
            entered = true;
            var requested = FocusProcesses.ParseKeys(candidates);
            if (requested.Length == 0) throw new InvalidOperationException("No identifiable audio processes are available.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            string matches = await _match(checked((int)await ReadAsync(deadline.Token)), requested, deadline.Token);
            using var reply = context.CreateReplyWriter("s");
            reply.WriteString(matches);
            context.Reply(reply.CreateMessage());
        }
        catch (Exception ex)
        {
            string message = ex is InvalidOperationException ? ex.Message : "The compositor could not report a focused application.";
            try { context.ReplyError("org.openxlr.Desktop.Unavailable", message); }
            catch (Exception) { /* the requesting connection has already closed */ }
        }
        finally
        {
            if (entered) _oneRequest.Release();
            context.Dispose();
        }
    }

    internal async Task<uint> ReadAsync(CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        if (FlatpakEnvironment.IsFlatpak) throw new InvalidOperationException("Focused routing is unavailable in the Flatpak sandbox.");
        FocusCompositor source = CompositorFocus.Select();
        return source == FocusCompositor.KWin ? await ReadKWinAsync(deadline.Token)
            : await CompositorFocus.ReadAsync(source, deadline.Token);
    }

    private async Task<uint> ReadKWinAsync(CancellationToken cancel)
    {
        const string scriptName = "openxlr-focus-query";
        string file = OpenXlrPaths.ConfigFile("desktop-focus.js");
        // The scripting calls ride a connection of their own. A compositor
        // that answers late costs this one query, not the shortcut session
        // that the shared connection holds; KWin still reports back there.
        using var scripting = new DBusConnection(DesktopBus.SessionAddress());
        var kwin = new DesktopBus(scripting);
        try
        {
            await scripting.ConnectAsync().AsTask().WaitAsync(cancel);
            _kwinOwner = await kwin.Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
                "GetNameOwner", "s", (ref MessageWriter w) => w.WriteString("org.kde.KWin"),
                static (message, _) => message.GetBodyReader().ReadString(), cancel);
            await Unload(cancel);
            _cookie = Guid.NewGuid().ToString("N");
            _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Serialize every data value as a JavaScript string literal. No
            // window titles, executable paths or user text enter the script.
            OpenXlrPaths.WriteAtomic(file, $"""
                const window = workspace.activeWindow;
                callDBus({JsonSerializer.Serialize(bus.Connection.UniqueName)}, "/org/openxlr/Desktop",
                    "org.openxlr.Desktop", "ReportFocus", {JsonSerializer.Serialize(_cookie)}, String(window ? window.pid : 0));
                """);
            int id = await kwin.Call("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss",
                (ref MessageWriter w) => { w.WriteString(file); w.WriteString(scriptName); },
                static (message, _) => message.GetBodyReader().ReadInt32(), cancel);
            if (id < 0) throw new InvalidOperationException("KWin refused the focus query");
            await kwin.Empty("org.kde.KWin", "/Scripting/Script" + id.ToString(CultureInfo.InvariantCulture),
                "org.kde.kwin.Script", "run", cancel: cancel);
            return await _answer.Task.WaitAsync(cancel);
        }
        finally
        {
            _cookie = _kwinOwner = null;
            _answer = null;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await Unload(cleanup.Token);
            }
            catch (Exception) { /* next query removes a script left by a disappearing compositor */ }
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Task Unload(CancellationToken token) => kwin.Call("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting",
            "unloadScript", "s", (ref MessageWriter w) => w.WriteString(scriptName),
            static (message, _) => message.GetBodyReader().ReadBool(), token);
    }
}
