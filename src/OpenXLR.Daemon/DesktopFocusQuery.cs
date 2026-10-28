using System.Globalization;
using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>The desktop UI owns portal sessions and the compositor connection.</summary>
internal static class DesktopFocusQuery
{
    private static int _pending;
    internal static string[] ReadAudioProcesses(string[] candidates)
    {
        if (candidates.Length is 0 or > 1024 || candidates.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("no bounded list of audio processes is available");
        if (Interlocked.Exchange(ref _pending, 1) != 0)
            throw new InvalidOperationException("another focused-application request is still running");
        try
        {
            ProcessResult reply = ProcessRunner.Run("gdbus", ["call", "--session", "--dest", "org.openxlr.Desktop",
                "--object-path", "/org/openxlr/Desktop", "--method", "org.openxlr.Desktop.GetFocusedAudioProcesses",
                string.Join(',', candidates)], TimeSpan.FromSeconds(7), stdoutCap: 32768, stderrCap: 4096);
            if (!reply.Ok) throw new InvalidOperationException("the desktop host bridge could not identify the focused audio application: " + reply.Stderr.Trim()[..Math.Min(reply.Stderr.Trim().Length, 512)]);
            return ParseAudioProcesses(reply.StdoutText, candidates);
        }
        finally { Volatile.Write(ref _pending, 0); }
    }

    internal static string[] ParseAudioProcesses(string text, string[] candidates)
    {
        string value = text.Trim();
        if (!value.StartsWith("('", StringComparison.Ordinal) || !value.EndsWith("',)", StringComparison.Ordinal) || value.Length > 32768)
            throw new InvalidOperationException("the host returned an invalid audio process list");
        string[] parts = value[2..^3].Split(',');
        if (parts.Length is 0 or > 1024) throw new InvalidOperationException("the host returned too many audio processes");
        return parts.Select(part => candidates.Contains(part) && part.Length > 0
            ? part : throw new InvalidOperationException("the host returned an unrelated audio process")).Distinct().ToArray();
    }
    internal static int Read()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0)
            throw new InvalidOperationException("another focused-application request is still running");
        try
        {
            ProcessResult reply = ProcessRunner.Run("gdbus", ["call", "--session", "--dest", "org.openxlr.Desktop",
                "--object-path", "/org/openxlr/Desktop", "--method", "org.openxlr.Desktop.GetFocusedProcess"], TimeSpan.FromSeconds(4),
                stdoutCap: 4096, stderrCap: 4096);
            if (!reply.Ok)
            {
                const string prefix = "GDBus.Error:org.openxlr.Desktop.Unavailable:";
                int at = reply.Stderr.IndexOf(prefix, StringComparison.Ordinal);
                string detail = at < 0 ? "focused routing requires the OpenXLR window running with Desktop keys enabled and a configured compositor focus source"
                    : reply.Stderr[(at + prefix.Length)..].Trim();
                throw new InvalidOperationException(detail.Length <= 512 ? detail : detail[..512]);
            }
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
