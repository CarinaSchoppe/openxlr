using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Explicitly enabled host execution, using the immutable installed Flatpak files.</summary>
internal static class FocusHost
{
    internal const string Command = "focus-host";
    internal const string Script = """
        set -eu
        files=$2
        if [ -z "$files" ]; then files="$(flatpak info --show-location io.github.emaspa.openxlr)/files"; fi
        unset FLATPAK_ID DOTNET_ROOT XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME
        export DOTNET_ROOT="$files/lib/dotnet"
        exec "$DOTNET_ROOT/dotnet" "$files/lib/openxlr/ui/OpenXLR.UI.dll" focus-host "$1"
        """;

    internal static async Task<string> ReadAsync(string candidates, CancellationToken cancel)
    {
        _ = FocusProcesses.ParseKeys(candidates);
        if (Environment.GetEnvironmentVariable("OPENXLR_FOCUS_HOST") != "1")
            throw new InvalidOperationException("Flatpak focused routing needs the optional host bridge. See the focused routing setup in the manual.");
        var arguments = new List<string> { "--host", "--watch-bus" };
        foreach (string name in new[] { "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "XDG_SESSION_TYPE", "SWAYSOCK", "HYPRLAND_INSTANCE_SIGNATURE", "NIRI_SOCKET", "DISPLAY", "WAYLAND_DISPLAY" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is null) continue;
            if (value.Length > 4096 || Array.Exists(value.ToCharArray(), char.IsControl))
                throw new InvalidOperationException("Invalid compositor session environment.");
            arguments.Add("--env=" + name + "=" + value);
        }
        arguments.AddRange(["sh", "-c", Script, "openxlr-focus-host", candidates,
            File.Exists("/.flatpak-info") ? ApplicationFiles(File.ReadAllText("/.flatpak-info")) : ""]);
        ProcessResult result = await ProcessRunner.RunAsync("flatpak-spawn", arguments, TimeSpan.FromSeconds(5), 32768, 4096, cancel: cancel);
        if (!result.Ok) throw new InvalidOperationException("The Flatpak focus host bridge failed. Check its host permission and the compositor setup.");
        string text = result.StdoutText.Trim();
        FocusProcesses.Candidate[] requested = FocusProcesses.ParseKeys(candidates), matched = FocusProcesses.ParseKeys(text);
        foreach (var pid in matched)
            if (Array.IndexOf(requested, pid) < 0) throw new InvalidOperationException("The host returned an unrelated audio process.");
        return string.Join(',', matched);
    }

    internal static string ApplicationFiles(string info)
    {
        bool instance = false;
        string? path = null;
        foreach (string line in info.Split('\n'))
        {
            if (line.StartsWith('[')) instance = line == "[Instance]";
            else if (instance && line.StartsWith("app-path=", StringComparison.Ordinal))
            {
                if (path is not null) throw new InvalidOperationException("The Flatpak installation path is ambiguous.");
                path = line[9..];
            }
        }
        if (path is null || !Path.IsPathFullyQualified(path) || path.Length > 4096 || Array.Exists(path.ToCharArray(), char.IsControl))
            throw new InvalidOperationException("The running Flatpak did not identify its installed files.");
        return path;
    }

    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != Command) return false;
        try
        {
            if (args.Length != 2) throw new InvalidOperationException("Run the focus helper with one candidate list.");
            FocusProcesses.Candidate[] candidates = FocusProcesses.ParseKeys(args[1]);
            Console.WriteLine((FlatpakEnvironment.IsFlatpak ? ReadAsync(args[1], CancellationToken.None) : QueryAsync(candidates)).GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is InvalidOperationException ? ex.Message : "The host could not query the focused application.");
            Environment.ExitCode = 2;
        }
        return true;
    }

    private static async Task<string> QueryAsync(FocusProcesses.Candidate[] candidates)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var connection = new DBusConnection(DesktopBus.SessionAddress());
        await connection.ConnectAsync().AsTask().WaitAsync(deadline.Token);
        var focus = new DesktopFocus(new DesktopBus(connection));
        connection.AddMethodHandler(focus);
        uint pid = await focus.ReadAsync(deadline.Token);
        return await FocusProcesses.MatchKeysAsync(checked((int)pid), candidates, deadline.Token);
    }
}
