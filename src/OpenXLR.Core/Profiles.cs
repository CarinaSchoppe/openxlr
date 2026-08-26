using System.Text.Json;
using System.Text.Json.Serialization;
using OpenXLR.Core.Devices;

namespace OpenXLR.Core;

/// <summary>
/// The mixer half of a profile: a scene, not a machine configuration. App
/// routing, the registry, and enforced system defaults deliberately stay
/// global; a profile recalls what a session sounds like, not how the
/// machine is wired into the desktop.
/// </summary>
public sealed record MixerScene
{
    public Dictionary<string, double> MixVolumes { get; init; } = [];
    public List<string> MixMuted { get; init; } = [];
    /// <summary>"channel|mix" to level.</summary>
    public Dictionary<string, double> Levels { get; init; } = [];
    public List<string> ChannelMuted { get; init; } = [];
    public List<string> MonitorOutputs { get; init; } = [];
    public bool AuxPortEnabled { get; init; }
    public double? OutputVolume { get; init; }
}

/// <summary>
/// A named snapshot of the whole rig: the hardware DSP state and the mixer
/// scene. Either half may be absent (saved without a device connected, or
/// without the mixer built) and loading applies whatever is present.
/// </summary>
public sealed record Profile
{
    public DeviceState? Device { get; init; }
    public MixerScene? Mixer { get; init; }
}

/// <summary>
/// Named profiles as single JSON files under the XDG config directory. The
/// file name is the profile name (sanitized), so the store needs no index
/// and survives hand-editing.
/// </summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Dir
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "openxlr", "profiles");
        }
    }

    /// <summary>
    /// A profile name reduced to something that is safe as a file name and
    /// stable across save/load. Returns null when nothing usable remains.
    /// </summary>
    public static string? SanitizeName(string? name)
    {
        if (name is null) return null;
        var kept = new string(name.Trim()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        return kept.Length is 0 or > 60 ? null : kept;
    }

    public static IReadOnlyList<string> List()
    {
        try
        {
            return [.. Directory.EnumerateFiles(Dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    public static void Save(string name, Profile profile)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, name + ".json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static Profile? Load(string name)
    {
        string path = Path.Combine(Dir, name + ".json");
        try { return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static bool Delete(string name)
    {
        string path = Path.Combine(Dir, name + ".json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
