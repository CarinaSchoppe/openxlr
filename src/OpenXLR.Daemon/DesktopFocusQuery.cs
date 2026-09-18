using System.Globalization;
using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>The desktop UI owns portal sessions and the compositor connection.</summary>
internal static class DesktopFocusQuery
{
    private static int _pending;
    internal static int Read()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0)
            throw new InvalidOperationException("another focused-application request is still running");
        try
        {
            ProcessResult reply = ProcessRunner.Run("gdbus", ["call", "--session", "--dest", "org.openxlr.Desktop",
                "--object-path", "/org/openxlr/Desktop", "--method", "org.openxlr.Desktop.GetFocusedProcess"], TimeSpan.FromSeconds(4),
                stdoutCap: 4096, stderrCap: 4096);
            if (!reply.Ok) throw new InvalidOperationException("focused routing requires the OpenXLR window running with Desktop keys enabled on KDE Plasma");
            return Parse(reply.StdoutText);
        }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new InvalidOperationException("focused routing needs gdbus (the GLib command-line tools)", ex); }
        finally { Volatile.Write(ref _pending, 0); }
    }

    internal static int Parse(string text)
    {
        string value = text.Trim();
        if (value.StartsWith("(uint32 ", StringComparison.Ordinal) && value.EndsWith(",)", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(8, value.Length - 10), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0)
            return pid;
        throw new InvalidOperationException("the desktop did not report a focused application");
    }
}
