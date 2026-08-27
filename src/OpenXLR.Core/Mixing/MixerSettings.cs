using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What survives a restart: fader levels, mutes, device choices, and the per
/// application channel assignments the user made by hand. Stored as JSON under
/// the XDG config directory.
///
/// Only user decisions are persisted. The graph itself is rebuilt from scratch
/// each run, so a stale file can never leave orphaned PipeWire nodes behind.
/// </summary>
public sealed record MixerSettings
{
    /// <summary>Mix id to master volume.</summary>
    public Dictionary<string, double> MixVolumes { get; init; } = [];

    /// <summary>Mix ids that are muted.</summary>
    public List<string> MixMuted { get; init; } = [];

    /// <summary>"channel|mix" to level.</summary>
    public Dictionary<string, double> Levels { get; init; } = [];

    /// <summary>"channel|mix" entries that are muted.</summary>
    public List<string> ChannelMuted { get; init; } = [];

    /// <summary>Single monitor output from older files; superseded by MonitorOutputs.</summary>
    public string? MonitorOutput { get; init; }

    /// <summary>All selected monitor outputs (the monitor mix can feed several).</summary>
    public List<string> MonitorOutputs { get; init; } = [];

    /// <summary>Application identity to channel id, from manual assignments.</summary>
    public Dictionary<string, string> AppOverrides { get; init; } = [];

    /// <summary>Every app ever seen, so the list survives silence and restarts.</summary>
    public List<SavedApp> KnownApps { get; init; } = [];

    /// <summary>Software low cut on the first XLR channel (0, 80, or 120 Hz).</summary>
    public int LowCutHz { get; init; }

    /// <summary>
    /// Whether the Aux mix feeds the USB Aux port. Null in files written before
    /// the Aux mix existed; migrated from the old monitor-destination choice.
    /// </summary>
    public bool? AuxPortEnabled { get; init; }

    /// <summary>
    /// Devices to enforce as the system defaults, Wave Link style: when set,
    /// the daemon re-asserts them every sweep so nothing else can steal them.
    /// Null means do not enforce.
    /// </summary>
    public string? EnforcedDefaultSink { get; init; }
    public string? EnforcedDefaultSource { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>~/.config/openxlr/mixer.json, honouring XDG_CONFIG_HOME.</summary>
    public static string DefaultPath
    {
        get
        {
            string baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "openxlr", "mixer.json");
        }
    }

    /// <summary>Read settings, or null when there is no file or it is unreadable.</summary>
    public static MixerSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MixerSettings>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;   // a corrupt file must not stop the daemon starting
        }
    }

    /// <summary>Write atomically so a crash mid-write cannot corrupt the file.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a settings write is not worth taking the daemon down.
        }
    }
}

/// <summary>A remembered application: identity, display label, channel.</summary>
public sealed record SavedApp(string Identity, string Label, string ChannelId);
