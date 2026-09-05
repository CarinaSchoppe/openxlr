namespace OpenXLR.Core.Mixing;

/// <summary>
/// One plugin in a channel's insert chain. Inserts sit in the channel path
/// after the software low cut and ClipGuard, before the fan-out to the
/// mixes, so every mix hears the processed signal. Isolated native hosts
/// run LV2 processing and its optional vendor UI; a bypassed insert is
/// simply left out of the chain until re-enabled.
/// </summary>
public sealed record InsertDefinition
{
    /// <summary>Stable id for this slot (survives reorders and restarts).</summary>
    public required string Id { get; init; }

    /// <summary>Plugin format: "lv2" or "vst3".</summary>
    public required string Kind { get; init; }

    /// <summary>The installed LV2 plugin URI.</summary>
    public required string Plugin { get; init; }

    /// <summary>Display name, captured from the catalog when added.</summary>
    public string? Label { get; init; }

    public bool Bypass { get; init; }

    /// <summary>Control values by port symbol; ports not listed keep defaults.</summary>
    public Dictionary<string, double> Params { get; init; } = [];

    /// <summary>
    /// Opaque format-native state, base64 encoded. Parameters remain separate
    /// for transparent editing and migration; hosts may restore both.
    /// </summary>
    public string? State { get; init; }

    /// <summary>Auxiliary input bus ID to stable OpenXLR signal-source ID.</summary>
    public Dictionary<string, string> Sidechains { get; init; } = [];
}

/// <summary>An insert as pushed to clients: its definition plus live status.</summary>
public sealed record InsertStatus(InsertDefinition Insert, string? Error,
    IReadOnlyDictionary<string, double>? Meters = null,
    int LatencySamples = 0,
    string Status = "ready",
    PluginRecoveryStatus? Recovery = null);

/// <summary>A control port of a plugin, enough to build a sensible slider.</summary>
public sealed record PluginParam(
    string Symbol, string Name,
    double Min, double Max, double Default,
    bool Toggled, bool Integer, bool Logarithmic, bool Enumeration,
    IReadOnlyList<ScalePoint> ScalePoints,
    /// <summary>LV2 units URI suffix or custom unit label (for example hz, ms, gain).</summary>
    string Unit);

public sealed record ScalePoint(string Label, double Value);

/// <summary>A plugin the catalog offers for insertion.</summary>
public sealed record PluginInfo(
    string Kind, string Plugin, string Name, string Category,
    int AudioIns, int AudioOuts,
    /// <summary>Symbols of the first audio input and output ports (filter-chain link endpoints).</summary>
    string InputSymbol, string OutputSymbol,
    IReadOnlyList<PluginParam> Params,
    IReadOnlyList<string> RequiredFeatures,
    /// <summary>All audio input and output port symbols, in port order (stereo chains link both).</summary>
    IReadOnlyList<string> InputSymbols,
    IReadOnlyList<string> OutputSymbols,
    bool HasNativeUi = false,
    bool SupportsState = false,
    int LatencySamples = 0,
    IReadOnlyList<PluginBusInfo>? AuxiliaryInputs = null,
    string ScanStatus = "ready",
    string? ScanError = null,
    /// <summary>Scanner-resolved module path; never accepted from an API insert request.</summary>
    string? ModulePath = null);

/// <summary>One non-main plugin input bus exposed for sidechain routing.</summary>
public sealed record PluginBusInfo(string Id, string Name, int Channels, bool DefaultActive);
