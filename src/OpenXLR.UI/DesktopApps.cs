using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>An installed application from a .desktop entry.</summary>
public sealed record InstalledApp(string Name, string Identity)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates installed applications by parsing .desktop files (system, user,
/// and flatpak locations). The identity is the guess for what the app's audio
/// streams will report as their process binary: the Exec line's program name,
/// or the last segment of a flatpak app id. A wrong guess is harmless; the app
/// just registers again under its real identity when it first plays.
/// </summary>
public static class DesktopApps
{
    public static IReadOnlyList<InstalledApp> Scan()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] dirs =
        [
            "/usr/share/applications",
            "/usr/local/share/applications",
            "/var/lib/flatpak/exports/share/applications",
            Path.Combine(home, ".local/share/applications"),
            Path.Combine(home, ".local/share/flatpak/exports/share/applications"),
        ];

        var byIdentity = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                InstalledApp? app = Parse(file);
                if (app is not null) byIdentity.TryAdd(app.Identity, app);
            }
        }
        return [.. byIdentity.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static InstalledApp? Parse(string path)
    {
        string? name = null, exec = null;
        bool inEntry = false, noDisplay = false, isApp = false;
        try
        {
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (inEntry) break;                        // next section: done
                    inEntry = line == "[Desktop Entry]";
                    continue;
                }
                if (!inEntry) continue;
                if (line.StartsWith("Name=", StringComparison.Ordinal)) name ??= line[5..];
                else if (line.StartsWith("Exec=", StringComparison.Ordinal)) exec ??= line[5..];
                else if (line == "NoDisplay=true") noDisplay = true;
                else if (line == "Type=Application") isApp = true;
            }
        }
        catch (IOException) { return null; }

        if (!isApp || noDisplay || name is null || exec is null) return null;
        string? identity = IdentityFromExec(exec);
        return identity is null ? null : new InstalledApp(name, identity);
    }

    /// <summary>The program the Exec line really starts, unwrapped.</summary>
    private static string? IdentityFromExec(string exec)
    {
        string[] tokens = exec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string tok = tokens[i];
            if (tok.StartsWith('%')) continue;                          // field codes
            string bin = Path.GetFileName(tok.Trim('"'));
            if (bin is "env" or "sh" or "bash" or "gtk-launch") continue;
            if (bin.Contains('=')) continue;                            // env VAR=...
            if (bin == "flatpak")
            {
                // flatpak run [options] com.vendor.App → last id segment is
                // usually the binary name inside the sandbox.
                string? appId = tokens.Skip(i + 1).LastOrDefault(t => t.Contains('.') && !t.StartsWith('-') && !t.StartsWith('%'));
                string[]? parts = appId?.Split('.');
                if (parts is null or { Length: < 2 }) return null;
                // com.discordapp.Discord -> Discord, but com.spotify.Client ->
                // spotify: a generic tail means the vendor segment is the name.
                string tail = parts[^1];
                return tail is "Client" or "client" or "Desktop" or "desktop" or "App" or "app"
                    ? parts[^2] : tail;
            }
            return bin;
        }
        return null;
    }
}
