using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>EWMH focus on X11. Never used to infer the focused Wayland application from Xwayland.</summary>
internal static class X11Focus
{
    private static readonly Regex Window = new(@"^_NET_ACTIVE_WINDOW\(WINDOW\) = 0x([0-9a-fA-F]{1,8})$", RegexOptions.CultureInvariant);
    private static readonly Regex Pid = new(@"^_NET_WM_PID\(CARDINAL\) = ([0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Machine = new("^WM_CLIENT_MACHINE\\(STRING\\) = \"([A-Za-z0-9._-]+)\"$", RegexOptions.CultureInvariant);

    internal static async Task<uint> ReadAsync(CancellationToken cancel, Func<string, string?> environment, Func<string?>? hostname = null)
    {
        string display = environment("DISPLAY") ?? "";
        if (string.IsNullOrWhiteSpace(display) || display.Length > 256 || Array.Exists(display.ToCharArray(), char.IsControl))
            throw new InvalidOperationException("X11 focused routing needs this session's DISPLAY.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            uint window = ParseWindow(await Query("-root", "-f", "_NET_ACTIVE_WINDOW", "32x", " = $0\n", "_NET_ACTIVE_WINDOW"));
            uint pid = ParseProcess(await Query("-id", "0x" + window.ToString("x", CultureInfo.InvariantCulture),
                "_NET_WM_PID", "WM_CLIENT_MACHINE"), Environment.MachineName, (hostname ?? FullHostname)());
            if (window != ParseWindow(await Query("-root", "-f", "_NET_ACTIVE_WINDOW", "32x", " = $0\n", "_NET_ACTIVE_WINDOW")))
                throw new InvalidOperationException("The focused X11 window changed during the query; try again.");
            return pid;
        }
        catch (Win32Exception ex) { throw new InvalidOperationException("X11 focused routing needs the xprop command installed.", ex); }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        { throw new InvalidOperationException("X11 did not answer the focus query within two seconds.", ex); }

        async Task<string> Query(params string[] args)
        {
            ProcessResult reply = await ProcessRunner.RunAsync("xprop", ["-display", display, .. args], TimeSpan.FromSeconds(2),
                4096, 4096, cancel: deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!reply.Ok) throw new InvalidOperationException("xprop did not return a complete focused-window reply.");
            return reply.StdoutText;
        }
    }

    private static string? FullHostname()
    {
        try { return File.ReadAllText("/proc/sys/kernel/hostname").Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static uint ParseWindow(string text)
    {
        Match match = Window.Match(text.Trim());
        if (match.Success && uint.TryParse(match.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint id) && id != 0)
            return id;
        throw new InvalidOperationException("The X11 window manager did not report an active application window.");
    }

    internal static uint ParseProcess(string text, string localMachine, string? fullMachine = null)
    {
        string[] lines = text.Trim().Split('\n');
        if (lines.Length == 2 && Pid.Match(lines[0].Trim()) is { Success: true } pid
            && Machine.Match(lines[1].Trim()) is { Success: true } machine
            && (string.Equals(machine.Groups[1].Value, localMachine, StringComparison.OrdinalIgnoreCase)
                || fullMachine is not null && string.Equals(machine.Groups[1].Value, fullMachine, StringComparison.OrdinalIgnoreCase))
            && uint.TryParse(pid.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) && value is > 0 and <= int.MaxValue)
            return value;
        throw new InvalidOperationException("The focused X11 window did not identify one process on this computer.");
    }
}
