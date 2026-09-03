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

    /// <summary>Currently "lv2"; unknown formats are rejected.</summary>
    public required string Kind { get; init; }

    /// <summary>The installed LV2 plugin URI.</summary>
    public required string Plugin { get; init; }

    /// <summary>Display name, captured from the catalog when added.</summary>
    public string? Label { get; init; }

    public bool Bypass { get; init; }

    /// <summary>Control values by port symbol; ports not listed keep defaults.</summary>
    public Dictionary<string, double> Params { get; init; } = [];
}

/// <summary>An insert as pushed to clients: its definition plus live status.</summary>
public sealed record InsertStatus(InsertDefinition Insert, string? Error,
    IReadOnlyDictionary<string, double>? Meters = null);

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
    bool HasNativeUi = false);
