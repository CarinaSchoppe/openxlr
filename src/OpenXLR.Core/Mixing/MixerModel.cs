namespace OpenXLR.Core.Mixing;

/// <summary>
/// The submixer model, mirroring what Wave Link provides: application audio is
/// grouped into channels, and every channel feeds every mix at its own level.
/// One mix is what you hear (monitor), the others are published as virtual
/// capture devices other apps can select (stream/chat).
///
/// In PipeWire this becomes: one null sink per channel (apps play into it), one
/// null sink per mix, and a pw-loopback per channel-per-mix carrying the
/// channel's monitor into the mix at the cell's volume. Pulling a fader to zero
/// removes that source from that mix only.
/// </summary>
public sealed record MixerConfig
{
    public required IReadOnlyList<MixDefinition> Mixes { get; init; }
    public required IReadOnlyList<ChannelDefinition> Channels { get; init; }

    /// <summary>
    /// The layout carried over from the user's Wave Link setup: three mixes
    /// (monitor / stream / mic-FX) and the standard channel set.
    /// </summary>
    public static MixerConfig Default() => new()
    {
        Mixes =
        [
            new MixDefinition("monitor", "Monitor", MixKind.Monitor) { Volume = 1.0 },
            new MixDefinition("stream", "Stream", MixKind.VirtualMic) { Volume = 1.0 },
            new MixDefinition("chat", "Chat", MixKind.VirtualMic) { Volume = 1.0 },
        ],
        Channels =
        [
            new ChannelDefinition("mic", "Mic") { Levels = Level(1.0, 1.0, 1.0) },
            new ChannelDefinition("game", "Game") { Levels = Level(0.5, 0.5, 0.5) },
            new ChannelDefinition("music", "Music") { Levels = Level(1.0, 1.0, 1.0) },
            new ChannelDefinition("browser", "Browser") { Levels = Level(1.0, 1.0, 1.0) },
            new ChannelDefinition("system", "System") { Levels = Level(0.6, 0.6, 0.6) },
            new ChannelDefinition("voicechat", "Voice Chat") { Levels = Level(1.0, 1.0, 1.0) },
            new ChannelDefinition("sfx", "SFX") { Levels = Level(1.0, 1.0, 1.0) },
        ],
    };

    private static Dictionary<string, double> Level(double monitor, double stream, double chat)
        => new() { ["monitor"] = monitor, ["stream"] = stream, ["chat"] = chat };
}

public enum MixKind
{
    /// <summary>What the user hears; routed to a physical output.</summary>
    Monitor,
    /// <summary>Published as a virtual capture device for OBS/Discord.</summary>
    VirtualMic,
}

public sealed record MixDefinition(string Id, string Name, MixKind Kind)
{
    public double Volume { get; init; } = 1.0;
    public bool Muted { get; init; }

    /// <summary>PipeWire node name of this mix's sink.</summary>
    public string SinkName => $"OpenXLR_mix_{Id}";
    /// <summary>PipeWire node name of the published virtual capture device.</summary>
    public string VirtualMicName => $"OpenXLR_{Id}";
}

public sealed record ChannelDefinition(string Id, string Name)
{
    /// <summary>Per-mix send level, keyed by mix id (0.0 = not in that mix).</summary>
    public IReadOnlyDictionary<string, double> Levels { get; init; } = new Dictionary<string, double>();
    /// <summary>Per-mix mute, keyed by mix id.</summary>
    public IReadOnlySet<string> MutedIn { get; init; } = new HashSet<string>();

    /// <summary>PipeWire node name of the sink applications play into.</summary>
    public string SinkName => $"OpenXLR_ch_{Id}";
}

/// <summary>Live mixer state pushed to clients.</summary>
public sealed record MixerState
{
    public required IReadOnlyList<MixStatus> Mixes { get; init; }
    public required IReadOnlyList<ChannelStatus> Channels { get; init; }

    /// <summary>node.name of the sink the monitor mix feeds, or null.</summary>
    public string? MonitorOutput { get; init; }

    /// <summary>node.name of the capture device feeding the mic channel, or null.</summary>
    public string? MicInput { get; init; }

    /// <summary>Volume of the selected output device (0..1), or null.</summary>
    public double? OutputVolume { get; init; }

    /// <summary>Volume of the default input device (0..1), or null.</summary>
    public double? InputVolume { get; init; }

    /// <summary>Enforced system default devices; null = not enforced.</summary>
    public string? EnforcedDefaultSink { get; init; }
    public string? EnforcedDefaultSource { get; init; }

    /// <summary>Application streams currently placed in channels.</summary>
    public IReadOnlyList<StreamAssignment> Streams { get; init; } = [];
}

public sealed record MixStatus(string Id, string Name, double Volume, bool Muted);

public sealed record ChannelStatus(string Id, string Name,
    IReadOnlyDictionary<string, double> Levels,
    IReadOnlyList<string> MutedIn);


/// <summary>An application stream and the channel it is playing into.</summary>
public sealed record StreamAssignment(int Id, int Serial, string Label, string Identity, string ChannelId);
