using System.Text.Json.Serialization;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

// The OpenXLR daemon control protocol: JSON messages over a WebSocket at
// ws://127.0.0.1:37890/ws. Clients (Avalonia UI, OpenDeck plugin, CLI) connect,
// receive a "state" message immediately and on every change, and send commands.
// One connection carries both the hardware controls and the submixer, so a
// fader move by one client is broadcast to all. camelCase on the wire.

/// <summary>Command from a client to the daemon.</summary>
public sealed record Command
{
    /// <summary>
    /// "getState" | "set" (device control) | "setLevel" | "setChannelMuted" |
    /// "setMixVolume" | "setMixMuted" | "assignStream" | "setMonitorOutput" |
    /// "setMicInput" | "setOutputVolume" | "setInputVolume" | "setEnforcedDefaults".
    /// </summary>
    [JsonPropertyName("cmd")] public string Cmd { get; init; } = "";

    /// <summary>For "set": the control name (see <see cref="ControlNames"/>).</summary>
    [JsonPropertyName("control")] public string? Control { get; init; }

    /// <summary>The value: number for levels, bool for toggles.</summary>
    [JsonPropertyName("value")] public System.Text.Json.JsonElement Value { get; init; }

    /// <summary>Mixer commands: which channel.</summary>
    [JsonPropertyName("channel")] public string? Channel { get; init; }

    /// <summary>Mixer commands: which mix.</summary>
    [JsonPropertyName("mix")] public string? Mix { get; init; }

    /// <summary>"assignStream": the PipeWire stream (sink-input) id to route.</summary>
    [JsonPropertyName("streamId")] public int? StreamId { get; init; }

    /// <summary>
    /// "setMonitorOutput" / "setMicInput": the PipeWire node.name to select, or
    /// null to disconnect that route.
    /// </summary>
    [JsonPropertyName("device")] public string? Device { get; init; }

    /// <summary>For "setMonitorOutputs": every output the monitor mix should feed.</summary>
    [JsonPropertyName("devices")] public List<string>? Devices { get; init; }

    /// <summary>For "assignApp": the application identity to route.</summary>
    [JsonPropertyName("identity")] public string? Identity { get; init; }

    /// <summary>For "assignApp": display label when pre-registering an app.</summary>
    [JsonPropertyName("label")] public string? Label { get; init; }

    /// <summary>"setEnforcedDefaults": devices to hold as system defaults (null = don't enforce).</summary>
    [JsonPropertyName("sink")] public string? Sink { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }

    /// <summary>"saveProfile" / "loadProfile" / "deleteProfile": the profile name.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>Full-state push from the daemon to all clients.</summary>
public sealed record StateMessage
{
    [JsonPropertyName("type")] public string Type => "state";
    [JsonPropertyName("connected")] public bool Connected { get; init; }
    [JsonPropertyName("device")] public DeviceDescriptor? Device { get; init; }
    [JsonPropertyName("capabilities")] public DeviceCapabilities? Capabilities { get; init; }
    [JsonPropertyName("state")] public DeviceState? State { get; init; }
    /// <summary>Submixer state; null until the mixer graph is built.</summary>
    [JsonPropertyName("mixer")] public MixerState? Mixer { get; init; }

    /// <summary>
    /// Every selectable sink and source in the graph, real or virtual, so
    /// clients can offer output and input pickers. Null when the mixer is off.
    /// </summary>
    [JsonPropertyName("devices")] public IReadOnlyList<AudioNode>? Devices { get; init; }

    /// <summary>Saved profile names, so any client can offer recall.</summary>
    [JsonPropertyName("profiles")] public IReadOnlyList<string>? Profiles { get; init; }
}

public sealed record DeviceDescriptor(string Vendor, string Model, string UsbId);

/// <summary>
/// Live levels, sent far more often than full state and kept deliberately small:
/// ids are "ch:&lt;channel&gt;" and "mix:&lt;mix&gt;", values are peaks in 0..1.
/// </summary>
public sealed record MetersMessage
{
    public MetersMessage(IReadOnlyDictionary<string, double[]> levels) => Levels = levels;

    [JsonPropertyName("type")] public string Type => "meters";
    [JsonPropertyName("levels")] public IReadOnlyDictionary<string, double[]> Levels { get; }
}

public sealed record ErrorMessage
{
    public ErrorMessage(string message) => Message = message;

    [JsonPropertyName("type")] public string Type => "error";
    [JsonPropertyName("message")] public string Message { get; }
}

/// <summary>Canonical control names accepted by "set".</summary>
public static class ControlNames
{
    public const string Gain = "gain";                       // int dB
    public const string Mute = "mute";                       // bool
    public const string LowCut = "lowCut";                   // bool
    public const string Expander = "expander";               // bool
    public const string VoiceTune = "voiceTune";             // bool
    public const string VoiceTuneStrength = "voiceTuneStrength"; // int 0..100
    public const string HpVolumeDb = "hpVolumeDb";           // number dB
    public const string LowImpedance = "lowImpedance";       // bool
    public const string Crossfade = "crossfade";             // int 0..200
    public const string Phantom = "phantom";                 // bool
    public const string ClipGuard = "clipGuard";             // bool
    public const string Compressor = "compressor";           // bool
    public const string OutHp1 = "outHp1";                   // bool
    public const string OutHp2 = "outHp2";                   // bool
    public const string OutUsbAux = "outUsbAux";             // bool
    public const string OutLineOut = "outLineOut";           // bool
    public const string AuxLevelDb = "auxLevelDb";           // number dB (-60..0)
    public const string AuxLevelLock = "auxLevelLock";       // bool
}

/// <summary>Reply to "getDiagnostics": raw vendor blocks for bug reports.</summary>
public sealed record DiagnosticsMessage(IReadOnlyDictionary<string, string> Blocks)
{
    [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type => "diagnostics";
}
