namespace OpenXLR.Core.Mixing;

/// <summary>
/// Decides which channel an application's audio stream belongs to, the job Wave
/// Link does with its per-app assignment list.
///
/// Matching looks at the process binary first, then the application name, then
/// the media name, because the binary is the most reliable field: an app can
/// leave application.name unset (it then inherits whatever the audio library
/// reports) while the binary always reflects the real process.
///
/// Proton and Wine are the known weak point: every Windows game routed through
/// them reports a binary like "wine64-preloader" or "wine", so binary matching
/// alone would lump them together. They are therefore treated as a hint that
/// this is a game rather than as an identity, and the media name (which often
/// carries the real title) is consulted before falling back to the Game channel.
/// </summary>
public sealed class StreamMatcher
{
    /// <summary>Ordered rules; the first match wins.</summary>
    public sealed record Rule(string ChannelId, IReadOnlyList<string> Patterns);

    private readonly List<Rule> _rules;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _fallbackChannel;

    public StreamMatcher(IEnumerable<Rule>? rules = null, string fallbackChannel = "system")
    {
        _rules = [.. rules ?? DefaultRules()];
        _fallbackChannel = fallbackChannel;
    }

    /// <summary>
    /// Defaults carried over from the user's Wave Link setup: browsers to
    /// Browser, Spotify and friends to Music, games and launchers to Game,
    /// chat apps to Voice Chat, everything else to System.
    /// </summary>
    public static IReadOnlyList<Rule> DefaultRules() =>
    [
        new("browser", ["chrome", "chromium", "firefox", "librewolf", "brave", "vivaldi", "edge", "epiphany"]),
        new("music", ["spotify", "youtube music", "ytmdesktop", "tidal", "rhythmbox", "elisa", "lollypop", "mpv", "vlc"]),
        new("voicechat", ["discord", "vesktop", "teamspeak", "mumble", "element", "signal", "telegram", "zoom", "slack"]),
        new("game", ["steam", "gamescope", "lutris", "heroic", "bottles", "minecraft", "hearthstone", "kingdomcome", "bloodlines", "expedition"]),
        new("sfx", ["soundboard", "sfx"]),
    ];

    /// <summary>Binaries that mean "a Windows game under a translation layer".</summary>
    private static readonly string[] WineLike =
        ["wine", "wine64", "wine-preloader", "wine64-preloader", "proton", "wineserver", "steam.exe"];

    /// <summary>
    /// Remember that a specific application belongs to a channel, overriding the
    /// rules. Keyed by the stream's identity so a Proton game keeps its channel
    /// even though its binary is shared with every other Proton game.
    /// </summary>
    /// <summary>Drop a remembered per-app choice (used when forgetting an app).</summary>
    public void RemoveOverride(string identity) => _overrides.Remove(identity);

    public void SetOverride(string identity, string channelId)
    {
        if (!string.IsNullOrWhiteSpace(identity)) _overrides[identity] = channelId;
    }

    public void ClearOverride(string identity) => _overrides.Remove(identity);

    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>The channel this stream should play into.</summary>
    public string Match(AudioStream stream)
    {
        if (_overrides.TryGetValue(stream.Identity, out string? pinned)) return pinned;

        // Binary first, then app name, then the media name.
        foreach (string field in new[] { stream.Binary, stream.AppName, stream.MediaName })
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            foreach (Rule rule in _rules)
                foreach (string pat in rule.Patterns)
                    if (field.Contains(pat, StringComparison.OrdinalIgnoreCase))
                        return rule.ChannelId;
        }

        // Wine and Proton report a shared binary, so treat them as games rather
        // than letting them fall through to System with every other unknown app.
        if (IsWineLike(stream.Binary) || IsWineLike(stream.AppName)) return "game";

        return _fallbackChannel;
    }

    private static bool IsWineLike(string? s) =>
        s is not null && WineLike.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One application playback stream in the graph.</summary>
public sealed record AudioStream(int Id, string? AppName, string? Binary, string? MediaName)
{
    /// <summary>PulseAudio sink-input id (PipeWire object.serial); used to move it.</summary>
    public int Serial { get; init; }

    /// <summary>
    /// Stable-ish key for remembering a per-app choice. Prefers the binary, but
    /// for Wine and Proton the binary is shared, so the media name is folded in
    /// to keep separate games apart.
    /// </summary>
    public string Identity
    {
        get
        {
            string bin = Binary ?? AppName ?? MediaName ?? "unknown";
            bool shared = bin.Contains("wine", StringComparison.OrdinalIgnoreCase) ||
                          bin.Contains("proton", StringComparison.OrdinalIgnoreCase);
            return shared && !string.IsNullOrWhiteSpace(MediaName) ? $"{bin}|{MediaName}" : bin;
        }
    }

    /// <summary>
    /// Application names that identify a runtime, not the actual app: Electron
    /// apps (Discord and friends) all report "Chromium", so the process binary
    /// is the truthful name for them.
    /// </summary>
    private static readonly string[] GenericAppNames =
        ["Chromium", "Chromium input", "Electron", "WEBRTC VoiceEngine", "Wine",
         "ALSA plug-in", "ringrtc", "libcanberra"];

    /// <summary>What to show in a picker.</summary>
    public string Label
    {
        get
        {
            bool generic = AppName is not { Length: > 0 } || AppName == "paplay" ||
                Array.Exists(GenericAppNames, g => AppName.Equals(g, StringComparison.OrdinalIgnoreCase));
            if (!generic) return AppName!;
            if (Binary is { Length: > 1 } && Binary != "paplay")
                return char.ToUpperInvariant(Binary[0]) + Binary[1..];
            return AppName is { Length: > 0 } ? AppName : MediaName ?? $"stream {Id}";
        }
    }
}
