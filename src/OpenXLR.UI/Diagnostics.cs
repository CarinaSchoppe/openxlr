using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// Bundles everything a useful bug report needs into one tar.gz a tester can
/// attach to an issue: app/device state, raw vendor blocks, the PipeWire
/// graph, daemon logs, configs and system info. Nothing is uploaded anywhere;
/// the file lands in the user's home directory.
/// </summary>
public static class Diagnostics
{
    public static async Task<string> CollectAsync(DaemonClient client)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string work = Path.Combine(Path.GetTempPath(), $"openxlr-diag-{stamp}");
        Directory.CreateDirectory(work);
        try
        {
            var meta = new StringBuilder();
            meta.AppendLine($"OpenXLR diagnostics {stamp}");
            meta.AppendLine($"version: {AppVersion.Current}");
            meta.AppendLine($"uname: {await RunAsync("uname", "-a")}");
            meta.AppendLine($"os-release: {TryReadFile("/etc/os-release")}");
            meta.AppendLine($"dotnet: {Environment.Version}");
            await File.WriteAllTextAsync(Path.Combine(work, "meta.txt"), meta.ToString());

            // Daemon views: the newest state push plus a fresh vendor-block dump.
            await File.WriteAllTextAsync(Path.Combine(work, "daemon-state.json"),
                client.LastStateJson ?? "no state received (daemon not running?)");
            var blocks = await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(work, "device-blocks.json"),
                blocks?.ToJsonString() ?? "unavailable (daemon not running or no device)");

            // Audio stack.
            await WriteCmd(work, "pw-dump.json", "pw-dump");
            await WriteCmd(work, "pactl-info.txt", "pactl", "info");
            await WriteCmd(work, "wpctl-status.txt", "wpctl", "status");
            await WriteCmd(work, "sinks.txt", "pactl", "list", "short", "sinks");
            await WriteCmd(work, "sources.txt", "pactl", "list", "short", "sources");
            await WriteCmd(work, "modules.txt", "pactl", "list", "short", "modules");
            await WriteCmd(work, "lsusb.txt", "lsusb");
            await WriteCmd(work, "journal.txt", "journalctl", "--user", "-u", "openxlr-daemon",
                "--since", "2 hours ago", "--no-pager");

            // Configs (device names only, nothing sensitive).
            CopyIfExists(UiSettings.ConfigDir, "mixer.json", work);
            CopyIfExists(UiSettings.ConfigDir, "ui.json", work);

            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                $"openxlr-diagnostics-{stamp}.tar.gz");
            await using (var fs = File.Create(outPath))
            await using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
                await TarFile.CreateFromDirectoryAsync(work, gz, includeBaseDirectory: false);
            return outPath;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WriteCmd(string dir, string file, string exe, params string[] args)
        => await File.WriteAllTextAsync(Path.Combine(dir, file), await RunAsync(exe, args));

    private static async Task<string> RunAsync(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            if (await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(15000)) is { } && !p.HasExited)
            { try { p.Kill(); } catch (InvalidOperationException) { } }
            return await outTask + await errTask;
        }
        catch (Exception ex) { return $"failed to run {exe}: {ex.Message}"; }
    }

    private static string TryReadFile(string path)
    {
        try { return File.ReadAllText(path).ReplaceLineEndings(" | "); }
        catch (IOException) { return "unreadable"; }
        catch (UnauthorizedAccessException) { return "unreadable"; }
    }

    private static void CopyIfExists(string dir, string file, string dest)
    {
        string src = Path.Combine(dir, file);
        if (File.Exists(src)) File.Copy(src, Path.Combine(dest, file), overwrite: true);
    }
}
