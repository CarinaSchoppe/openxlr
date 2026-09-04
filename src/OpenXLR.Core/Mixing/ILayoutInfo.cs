namespace OpenXLR.Core.Mixing;

/// <summary>
/// What a command validator needs to know about the live layout, kept
/// small so it can be faked in tests.
/// </summary>
public interface ILayoutInfo
{
    bool HasChannel(string id);
    bool HasMix(string id);
    /// <summary>An insert chain key: an XLR input id or "mix:&lt;id&gt;".</summary>
    bool IsInsertKey(string key);
    /// <summary>Remembered application identities (pinned assignments).</summary>
    int OverrideCount { get; }
}
