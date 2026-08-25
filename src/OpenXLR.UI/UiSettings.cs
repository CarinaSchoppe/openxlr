using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OpenXLR.UI;

/// <summary>
/// UI-side preferences (startup behaviour, tray), stored in
/// ~/.config/openxlr/ui.json. The mixer's own state lives in the daemon's
/// mixer.json; this file only holds what the window process needs.
/// </summary>
public sealed record UiSettings
{
    public bool StartDaemonAtLogin { get; init; }
    public bool OpenWindowAtLogin { get; init; }
    public bool MinimizeToTray { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ConfigDir
    {
        get
        {
            string baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "openxlr");
        }
    }

    private static string FilePath => Path.Combine(ConfigDir, "ui.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new UiSettings();
        }
        catch (Exception) { /* corrupt file must not stop the app */ }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception) { /* best effort */ }
    }
}

/// <summary>
/// Applies startup preferences to the system: a systemd user unit for the
/// daemon, an XDG autostart entry for the window. Paths point at the build
/// output; packaging will replace them with installed binaries later.
/// </summary>
public static class StartupIntegration
{
    private static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DaemonBinary =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "OpenXLR.Daemon", "bin", "Release", "net10.0", "OpenXLR.Daemon"));

    private static string UiBinary =>
        Path.Combine(AppContext.BaseDirectory, "OpenXLR.UI");

    private static string UnitPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x : Path.Combine(HomeDir, ".config"),
        "systemd", "user", "openxlr-daemon.service");

    private static string AutostartPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x : Path.Combine(HomeDir, ".config"),
        "autostart", "openxlr.desktop");

    public static void SetDaemonAtLogin(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
            File.WriteAllText(UnitPath, $"""
                [Unit]
                Description=OpenXLR audio daemon
                After=pipewire-pulse.service wireplumber.service

                [Service]
                ExecStart={DaemonBinary}
                Environment=OPENXLR_BUILD_MIXER=1
                Restart=on-failure
                RestartSec=3

                [Install]
                WantedBy=default.target
                """);
            Systemctl("daemon-reload");
            Systemctl("enable", "openxlr-daemon.service");
        }
        else
        {
            Systemctl("disable", "openxlr-daemon.service");
            try { File.Delete(UnitPath); } catch (IOException) { }
            Systemctl("daemon-reload");
        }
    }

    public static void SetWindowAtLogin(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartPath)!);
            File.WriteAllText(AutostartPath, $"""
                [Desktop Entry]
                Type=Application
                Name=OpenXLR
                Comment=OpenXLR mixer window
                Exec={UiBinary}
                Icon=openxlr
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
        }
        else
        {
            try { File.Delete(AutostartPath); } catch (IOException) { }
        }
    }

    private static void Systemctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--user");
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception) { /* best effort */ }
    }
}
