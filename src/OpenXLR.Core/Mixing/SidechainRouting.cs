namespace OpenXLR.Core.Mixing;

/// <summary>A stable signal source offered to compatible auxiliary buses.</summary>
public sealed record PluginSidechainSource(string Id, string Name, int Channels, bool Available = true);

/// <summary>Pure validation for sidechain routes before PipeWire is changed.</summary>
public static class SidechainRouting
{
    public static string? Validate(string target, string sourceId, int sourceChannels, int busChannels)
    {
        if (sourceChannels is < 1 or > 2 || busChannels is < 1 or > 2)
            return "sidechain uses an unsupported channel layout";
        string targetId = target.StartsWith("mix:", StringComparison.Ordinal)
            ? target : $"channel:{target}";
        if (targetId == sourceId) return "an insert cannot use its own output as a sidechain";

        // Every channel fans out into every mix. Feeding a mix back into any
        // channel insert would therefore close channel -> mix -> sidechain ->
        // channel even when its current fader happens to be muted.
        if (!target.StartsWith("mix:", StringComparison.Ordinal)
            && sourceId.StartsWith("mix:", StringComparison.Ordinal))
            return "a mix cannot sidechain a channel that feeds that mix";
        if (sourceChannels > busChannels)
            return "stereo-to-mono sidechain downmix is not supported";
        return null;
    }
}
