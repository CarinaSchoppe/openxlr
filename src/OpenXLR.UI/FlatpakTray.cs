using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;
using Tmds.DBus.Protocol;
using Pixmaps = Tmds.DBus.Protocol.Array<Tmds.DBus.Protocol.Struct<int, int, Tmds.DBus.Protocol.Array<byte>>>;

namespace OpenXLR.UI;

/// <summary>
/// The two-action tray under the app's allowed D-Bus namespace. Avalonia's
/// PID-based org.kde name cannot be owned through Flatpak's bus filter.
/// </summary>
internal sealed class FlatpakTray : IDisposable, IAsyncDisposable, IPathMethodHandler
{
    private const int MaximumMenuItems = 32;
    internal const string Service = FlatpakEnvironment.AppId + ".Tray";
    internal const string Watcher = "org.kde.StatusNotifierWatcher";
    private const string ItemInterface = "org.kde.StatusNotifierItem", MenuInterface = "com.canonical.dbusmenu";
    private readonly Action _show, _quit;
    private readonly Action? _soundboard;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private Task? _disposing;
    private readonly Action<Action> _post;
    public string Path => "/";
    public bool HandlesChildPaths => true;

    // Entry ids: 0 the root, 1 Show mixer, 2 the separator, 3 Quit, 4 Soundboard,
    // which stands after Show mixer.
    private const int LastEntry = 4;
    private static readonly int[] Children = [1, 4, 2, 3];

    internal FlatpakTray(Action show, Action quit, Action<Action>? post = null, Action? soundboard = null)
    {
        _show = show; _quit = quit; _soundboard = soundboard;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var connection = new DBusConnection(DesktopBus.SessionAddress());
                await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
                var bus = new DesktopBus(connection);
                connection.AddMethodHandler(this);
                await connection.RequestNameAsync(Service).WaitAsync(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
                using var changed = await connection.AddMatchAsync(new MatchRule
                {
                    Sender = "org.freedesktop.DBus", Path = "/org/freedesktop/DBus",
                    Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Type = MessageType.Signal,
                    Arg0 = Watcher,
                }, static (message, _) =>
                {
                    var reader = message.GetBodyReader(); reader.ReadString(); reader.ReadString();
                    return reader.ReadString();
                }, update =>
                {
                    if (!update.IsCompletion && update.Value.Length != 0) _ = RegisterAsync(bus);
                }, emitOnCapturedContext: false);
                await RegisterAsync(bus).ConfigureAwait(false);
                await connection.DisconnectedAsync().WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine("Flatpak tray: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RegisterAsync(DesktopBus bus)
    {
        if (_stop.IsCancellationRequested) return;
        try
        {
            await bus.Empty(Watcher, "/StatusNotifierWatcher", Watcher, "RegisterStatusNotifierItem", "s",
                (ref MessageWriter writer) => writer.WriteString(Service), _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        { Console.Error.WriteLine("Flatpak tray host: " + ex.Message); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static Dictionary<string, VariantValue> ItemProperties() => new()
    {
        ["Category"] = "ApplicationStatus", ["Id"] = FlatpakEnvironment.AppId,
        ["Title"] = "OpenXLR", ["Status"] = "Active", ["WindowId"] = 0u,
        ["IconName"] = FlatpakEnvironment.AppId, ["IconPixmap"] = new Pixmaps(),
        ["OverlayIconName"] = "", ["OverlayIconPixmap"] = new Pixmaps(),
        ["AttentionIconName"] = "", ["AttentionIconPixmap"] = new Pixmaps(), ["AttentionMovieName"] = "",
        ["ToolTip"] = VariantValue.Struct(FlatpakEnvironment.AppId, new Pixmaps(), "OpenXLR", ""),
        ["ItemIsMenu"] = false, ["Menu"] = new ObjectPath("/MenuBar"),
    };

    private static Dictionary<string, VariantValue> MenuProperties() => new()
    { ["Version"] = 3u, ["TextDirection"] = "ltr", ["Status"] = "normal", ["IconThemePath"] = VariantValue.Array(System.Array.Empty<string>()) };

    internal static Dictionary<string, VariantValue> Entry(int id) => id switch
    {
        0 => new() { ["children-display"] = "submenu" },
        1 => new() { ["label"] = Localizer.Text("ShowMixer"), ["enabled"] = true, ["visible"] = true },
        2 => new() { ["type"] = "separator", ["visible"] = true },
        3 => new() { ["label"] = Localizer.Text("QuitOpenXLR"), ["enabled"] = true, ["visible"] = true },
        4 => new() { ["label"] = Localizer.Text("Soundboard"), ["enabled"] = true, ["visible"] = true },
        _ => new(),
    };

    private static void Layout(ref MessageWriter writer, int id, int depth)
    {
        writer.WriteStructureStart(); writer.WriteInt32(id); writer.WriteDictionary(Entry(id));
        var children = writer.WriteArrayStart(DBusType.Variant);
        if (id == 0 && depth != 0)
            foreach (int child in Children)
            {
                writer.WriteSignature("(ia{sv}av)");
                Layout(ref writer, child, 0);
            }
        writer.WriteArrayEnd(children);
    }

    private static int[]? ReadMenuIds(ref Reader reader)
    {
        var ids = new List<int>();
        var end = reader.ReadArrayStart(DBusType.Int32);
        while (reader.HasNext(end))
        {
            if (ids.Count == MaximumMenuItems) return null;
            ids.Add(reader.ReadInt32());
        }
        return ids.ToArray();
    }

    private const string MenuXml = """
        <interface name="com.canonical.dbusmenu">
          <method name="GetLayout"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="as" direction="in"/><arg type="u" direction="out"/><arg type="(ia{sv}av)" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" direction="in"/><arg type="as" direction="in"/><arg type="a(ia{sv})" direction="out"/></method>
          <method name="GetProperty"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method>
          <method name="Event"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="in"/><arg type="u" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" direction="in"/><arg type="ai" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" direction="in"/><arg type="ai" direction="out"/><arg type="ai" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" direction="in"/><arg type="b" direction="out"/></method>
        </interface>
        """;
    private const string ItemXml = """
        <interface name="org.kde.StatusNotifierItem">
          <method name="Activate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
          <method name="ContextMenu"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
          <method name="Scroll"><arg type="i" direction="in"/><arg type="s" direction="in"/></method>
        </interface>
        """;
    private const string PropertiesXml = """
        <interface name="org.freedesktop.DBus.Properties">
          <method name="Get"><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method>
          <method name="GetAll"><arg type="s" direction="in"/><arg type="a{sv}" direction="out"/></method>
        </interface>
        """;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        string? iface = context.Request.InterfaceAsString, member = context.Request.MemberAsString;
        var reader = context.Request.GetBodyReader();
        if (context.Request.PathAsString is not ("/StatusNotifierItem" or "/MenuBar"))
        { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
        if (_stop.IsCancellationRequested) { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
        Action? action = null;
        List<Action>? grouped = null;
        if (iface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
        {
            using var reply = context.CreateReplyWriter("s");
            reply.WriteString("<node>" + (context.Request.PathAsString == "/MenuBar" ? MenuXml : ItemXml) + PropertiesXml + "</node>");
            context.Reply(reply.CreateMessage());
        }
        else if (iface == "org.freedesktop.DBus.Properties")
        {
            string target = reader.ReadString();
            var properties = target == ItemInterface ? ItemProperties() : target == MenuInterface ? MenuProperties() : null;
            if (properties is null) context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", "Unknown tray interface.");
            else if (member == "GetAll")
            { using var reply = context.CreateReplyWriter("a{sv}"); reply.WriteDictionary(properties); context.Reply(reply.CreateMessage()); }
            else if (member == "Get" && properties.TryGetValue(reader.ReadString(), out var value))
            { using var reply = context.CreateReplyWriter("v"); reply.WriteVariant(value); context.Reply(reply.CreateMessage()); }
            else context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "Unknown tray property.");
        }
        else if (iface == ItemInterface && member is "Activate" or "SecondaryActivate" or "ContextMenu" or "Scroll")
        {
            if (member is "Activate" or "SecondaryActivate") action = _show;
            using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage());
        }
        else if (iface == MenuInterface && member == "GetLayout")
        {
            int id = reader.ReadInt32(), depth = reader.ReadInt32();
            if (id is < 0 or > LastEntry) context.ReplyError("com.canonical.dbusmenu.Error.InvalidMenuId", "Unknown menu entry.");
            else
            {
                var reply = context.CreateReplyWriter("u(ia{sv}av)");
                try { reply.WriteUInt32(1); Layout(ref reply, id, depth); context.Reply(reply.CreateMessage()); }
                finally { reply.Dispose(); }
            }
        }
        else if (iface == MenuInterface && member == "GetGroupProperties")
        {
            if (ReadMenuIds(ref reader) is not { } ids)
            { context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Too many tray entries."); return ValueTask.CompletedTask; }
            if (ids.Length == 0) ids = [0, .. Children];
            using var reply = context.CreateReplyWriter("a(ia{sv})");
            var array = reply.WriteArrayStart(DBusType.Struct);
            foreach (int id in ids.Where(id => id is >= 0 and <= LastEntry).Distinct())
            { reply.WriteStructureStart(); reply.WriteInt32(id); reply.WriteDictionary(Entry(id)); }
            reply.WriteArrayEnd(array); context.Reply(reply.CreateMessage());
        }
        else if (iface == MenuInterface && member == "GetProperty")
        {
            int id = reader.ReadInt32(); string property = reader.ReadString();
            if (Entry(id).TryGetValue(property, out var value))
            { using var reply = context.CreateReplyWriter("v"); reply.WriteVariant(value); context.Reply(reply.CreateMessage()); }
            else context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "Unknown menu property.");
        }
        else if (iface == MenuInterface && member == "AboutToShowGroup")
        {
            if (ReadMenuIds(ref reader) is not { } ids)
            { context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Too many tray entries."); return ValueTask.CompletedTask; }
            using var reply = context.CreateReplyWriter("aiai");
            reply.WriteArray(System.Array.Empty<int>()); reply.WriteArray(ids.Where(id => id is < 0 or > LastEntry));
            context.Reply(reply.CreateMessage());
        }
        else if (iface == MenuInterface && member == "EventGroup")
        {
            grouped = []; var errors = new List<int>(); int count = 0;
            var end = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(end))
            {
                if (++count > MaximumMenuItems)
                { context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Too many tray events."); return ValueTask.CompletedTask; }
                reader.AlignStruct(); int id = reader.ReadInt32(); string kind = reader.ReadString();
                reader.ReadVariantValue(); reader.ReadUInt32();
                if (id is < 0 or > LastEntry) errors.Add(id);
                else if (kind == "clicked" && Clicked(id) is { } clicked) grouped.Add(clicked);
            }
            using var reply = context.CreateReplyWriter("ai"); reply.WriteArray(errors); context.Reply(reply.CreateMessage());
        }
        else if (iface == MenuInterface && member == "AboutToShow")
        { using var reply = context.CreateReplyWriter("b"); reply.WriteBool(false); context.Reply(reply.CreateMessage()); }
        else if (iface == MenuInterface && member == "Event")
        {
            int id = reader.ReadInt32(); string kind = reader.ReadString();
            if (kind == "clicked") action = Clicked(id);
            using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage());
        }
        else context.ReplyUnknownMethodError();
        if (action is not null) _post(action);
        if (grouped is { Count: > 0 }) _post(() => { foreach (var item in grouped) item(); });
        return ValueTask.CompletedTask;
    }

    private Action? Clicked(int id) => id switch { 1 => _show, 3 => _quit, 4 => _soundboard, _ => null };

    public void Dispose() => _ = StopAsync();
    public ValueTask DisposeAsync() => new(StopAsync());

    private Task StopAsync()
    {
        lock (_stop) return _disposing ??= FinishAsync();
    }

    private async Task FinishAsync()
    {
        _stop.Cancel();
        await _run.ConfigureAwait(false);
        _stop.Dispose();
    }
}
