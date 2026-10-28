using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OpenXLR.UI;

/// <summary>
/// The freedesktop icon theme lookup: an icon name to the image files that
/// draw it, best first. The desktop's own theme is searched first, then the
/// themes it inherits, then hicolor, then the unthemed pixmaps directories.
/// Within a theme, a directory made for the requested size comes first, then
/// the nearest sizes, a larger one preferred since it scales down cleanly.
/// PNG and SVG are the formats returned. Every lookup touches the disk, so
/// the window calls it off the UI thread. Themes and a bounded window of
/// recent lookup results are kept once read.
/// </summary>
public sealed class IconThemeLookup
{
    internal const int MaxIconLength = 4096;
    private const int MaxCachedLookups = 1024;
    private static readonly string[] Extensions = [".png", ".svg"];

    private readonly IReadOnlyList<string> _baseDirs;
    private readonly IReadOnlyList<string> _pixmapDirs;
    private readonly string? _theme;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Theme?> _themes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Name, int Size), IReadOnlyList<string>> _found = new();
    private readonly Lock _foundGate = new();
    private IReadOnlyList<Theme>? _chain;

    /// <param name="baseDirs">The directories themes live in, searched in order.</param>
    /// <param name="pixmapDirs">The directories searched for an unthemed icon last.</param>
    /// <param name="theme">The desktop's icon theme, or null for hicolor alone.</param>
    public IconThemeLookup(IReadOnlyList<string> baseDirs, IReadOnlyList<string> pixmapDirs, string? theme)
    {
        _baseDirs = baseDirs;
        _pixmapDirs = pixmapDirs;
        _theme = ValidName(theme) ? theme : null;
    }

    /// <summary>The theme the lookup starts from, or null.</summary>
    public string? ThemeName => _theme;

    /// <summary>The lookup for this session: its theme and its data directories.</summary>
    public static IconThemeLookup ForDesktop() => new(BaseDirectories(), PixmapDirectories(), DesktopTheme());

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> DataDirs()
    {
        string? dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dirs)) dirs = "/usr/local/share:/usr/share";
        return dirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted).Select(d => d.TrimEnd('/'));
    }

    /// <summary>
    /// <c>~/.icons</c>, the <c>icons</c> directory of <c>XDG_DATA_HOME</c> and
    /// of every <c>XDG_DATA_DIRS</c> entry, then the Flatpak exports and Snap's
    /// icons when the session did not list them.
    /// </summary>
    public static IReadOnlyList<string> BaseDirectories()
    {
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome)) dataHome = Path.Combine(Home, ".local/share");
        var dirs = new List<string> { Path.Combine(Home, ".icons"), Path.Combine(dataHome.TrimEnd('/'), "icons") };
        dirs.AddRange(DataDirs().Select(d => Path.Combine(d, "icons")));
        dirs.Add(Path.Combine(Home, ".local/share/flatpak/exports/share/icons"));
        dirs.Add("/var/lib/flatpak/exports/share/icons");
        dirs.Add("/var/lib/snapd/desktop/icons");
        return [.. dirs.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The <c>pixmaps</c> directory of every data directory, and <c>/usr/share/pixmaps</c>.</summary>
    public static IReadOnlyList<string> PixmapDirectories()
        => [.. DataDirs().Select(d => Path.Combine(d, "pixmaps")).Append("/usr/share/pixmaps").Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The icon theme the desktop wears, where a file says so: Plasma's
    /// <c>kdeglobals</c>, else GTK's <c>settings.ini</c>. Plasma without a
    /// choice wears Breeze. GNOME keeps its choice in dconf, which is not read
    /// here; its default theme draws no application icons of its own, so
    /// hicolor alone finds the same ones.
    /// </summary>
    public static string? DesktopTheme()
    {
        string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome) || !Path.IsPathRooted(configHome)) configHome = Path.Combine(Home, ".config");
        string? configDirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
        if (string.IsNullOrEmpty(configDirs)) configDirs = "/etc/xdg";
        string[] configs = [configHome, .. configDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)];

        string? Kde() => configs.Select(d => IniValue(Path.Combine(d, "kdeglobals"), "Icons", "Theme")).FirstOrDefault(ValidName);
        string? Gtk() => new[] { "gtk-4.0", "gtk-3.0" }
            .Select(g => IniValue(Path.Combine(configHome, g, "settings.ini"), "Settings", "gtk-icon-theme-name"))
            .FirstOrDefault(ValidName);

        bool plasma = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?
            .Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase) == true;
        return plasma ? Kde() ?? "breeze" : Gtk() ?? Kde();
    }

    /// <summary>One value of an INI-style file, or null.</summary>
    internal static string? IniValue(string path, string group, string key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            bool inGroup = false;
            foreach (string raw in File.ReadLines(path).Take(4000))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { inGroup = line == $"[{group}]"; continue; }
                if (!inGroup) continue;
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim() == key) return line[(eq + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // A theme or icon name is one path segment.
    private static bool ValidName(string? name)
        => name is { Length: > 0 and <= 255 } && name != "." && name != ".." &&
           !name.Contains('/') && !name.Any(char.IsControl);

    /// <summary>
    /// The files that draw this icon, best first, for a picture
    /// <paramref name="size"/> pixels square. An absolute path is its own
    /// answer when it is a PNG or SVG file. Empty when nothing draws it.
    /// </summary>
    public IReadOnlyList<string> Find(string? icon, int size)
    {
        if (string.IsNullOrWhiteSpace(icon)) return [];
        icon = icon.Trim();
        if (icon.Length > MaxIconLength) return [];
        size = Math.Clamp(size, 1, 1024);
        var key = (icon, size);
        if (_found.TryGetValue(key, out IReadOnlyList<string>? known)) return known;
        IReadOnlyList<string> found = Search(icon, size);
        lock (_foundGate)
        {
            if (_found.TryGetValue(key, out known)) return known;
            if (_found.Count >= MaxCachedLookups) _found.Clear();
            _found[key] = found;
        }
        return found;
    }

    private IReadOnlyList<string> Search(string icon, int size)
    {
        if (Path.IsPathRooted(icon))
            return Extensions.Contains(Path.GetExtension(icon).ToLowerInvariant()) && File.Exists(icon) && !Directory.Exists(icon)
                ? [icon] : [];
        // An Icon key should hold a bare name, but some carry the extension.
        string ext = Path.GetExtension(icon).ToLowerInvariant();
        if (ext is ".png" or ".svg" or ".xpm") icon = icon[..^ext.Length];
        if (!ValidName(icon)) return [];

        var files = new List<string>();
        foreach (Theme theme in Chain())
        {
            var found = new List<(double Rank, int Order, string File)>();
            foreach (ThemeDir dir in theme.Dirs)
                foreach (string path in dir.Paths)
                    foreach (string extension in Extensions)
                    {
                        string file = Path.Combine(path, icon + extension);
                        if (File.Exists(file)) found.Add((dir.Rank(size), found.Count, file));
                    }
            files.AddRange(found.OrderBy(f => f.Rank).ThenBy(f => f.Order).Select(f => f.File));
        }
        foreach (string dir in _pixmapDirs)
            foreach (string extension in Extensions)
            {
                string file = Path.Combine(dir, icon + extension);
                if (File.Exists(file)) files.Add(file);
            }
        return [.. files.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The desktop's theme, the themes it inherits depth first, then hicolor.</summary>
    internal IReadOnlyList<Theme> Chain()
    {
        lock (_gate)
        {
            if (_chain is not null) return _chain;
            var chain = new List<Theme>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Add(string name, int depth)
            {
                if (depth > 16 || !ValidName(name) || !visited.Add(name) || name == "hicolor") return;
                if (Load(name) is not { } theme) return;
                chain.Add(theme);
                foreach (string parent in theme.Inherits) Add(parent, depth + 1);
            }
            if (_theme is not null) Add(_theme, 0);
            if (Load("hicolor") is { } hicolor) chain.Add(hicolor);
            return _chain = chain;
        }
    }

    private Theme? Load(string name)
    {
        if (_themes.TryGetValue(name, out Theme? known)) return known;
        string[] roots = [.. _baseDirs.Select(b => Path.Combine(b, name)).Where(Directory.Exists)];
        string? index = roots.Select(r => Path.Combine(r, "index.theme")).FirstOrDefault(File.Exists);
        Theme? theme = index is null ? null : Theme.Read(name, index, roots);
        _themes[name] = theme;
        return theme;
    }

    /// <summary>One theme: the directories its index lists, where each exists, and the themes it inherits.</summary>
    internal sealed record Theme(string Name, IReadOnlyList<ThemeDir> Dirs, IReadOnlyList<string> Inherits)
    {
        public static Theme Read(string name, string index, IReadOnlyList<string> roots)
        {
            var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            Dictionary<string, string>? current = null;
            try
            {
                foreach (string raw in File.ReadLines(index).Take(100_000))
                {
                    string line = raw.Trim();
                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        string group = line[1..^1];
                        current = groups.TryGetValue(group, out var g) ? g : groups[group] = new(StringComparer.Ordinal);
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (current is not null && eq > 0) current.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            Dictionary<string, string> main = groups.GetValueOrDefault("Icon Theme") ?? [];
            static IEnumerable<string> List(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var dirs = new List<ThemeDir>();
            foreach (string sub in List(main.GetValueOrDefault("Directories")).Concat(List(main.GetValueOrDefault("ScaledDirectories"))).Distinct())
            {
                if (!groups.TryGetValue(sub, out var keys) || sub.Contains("..")) continue;
                string[] paths = [.. roots.Select(r => Path.Combine(r, sub)).Where(Directory.Exists)];
                if (paths.Length == 0) continue;
                int Int(string key, int fallback) => keys.TryGetValue(key, out string? v) && int.TryParse(v, out int n) && n > 0 ? n : fallback;
                int size = Int("Size", 0);
                if (size == 0) continue;
                int scale = Int("Scale", 1);
                string type = keys.GetValueOrDefault("Type") ?? "Threshold";
                dirs.Add(new ThemeDir(paths, size * scale, type,
                    Int("MinSize", size) * scale, Int("MaxSize", size) * scale, Int("Threshold", 2) * scale));
            }
            return new Theme(name, dirs, [.. List(main.GetValueOrDefault("Inherits"))]);
        }
    }

    /// <summary>One sized directory of a theme, in pixels.</summary>
    internal sealed record ThemeDir(IReadOnlyList<string> Paths, int Size, string Type, int MinSize, int MaxSize, int Threshold)
    {
        private bool Matches(int size) => Type switch
        {
            "Fixed" => Size == size,
            "Scalable" => MinSize <= size && size <= MaxSize,
            _ => Size - Threshold <= size && size <= Size + Threshold,
        };

        /// <summary>
        /// 0 for a directory made for this size; otherwise how far it is,
        /// counting a smaller one eight times over and losing a tie, since an
        /// image drawn larger than it was made blurs and one drawn smaller
        /// does not.
        /// </summary>
        public double Rank(int size)
        {
            if (Matches(size)) return 0;
            (int low, int high) = Type switch
            {
                "Scalable" => (MinSize, MaxSize),
                "Fixed" => (Size, Size),
                _ => (Size - Threshold, Size + Threshold),
            };
            return low > size ? low - size : (size - high) * 8 + 0.5;
        }
    }
}
