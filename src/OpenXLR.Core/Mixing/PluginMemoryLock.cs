using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>The running daemon's memory-lock allowance, inherited by its plugin hosts.</summary>
internal static class PluginMemoryLock
{
    // Match yabridge's memlock_min_safe_threshold in src/plugin/bridges/common.h.
    internal const long RecommendedBytes = 256L * 1024 * 1024;

    /// <summary>Bytes, -1 for unlimited, or null when the limit cannot be read.</summary>
    internal static long? ReadLimit()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return ParseLimit(File.ReadAllText("/proc/self/limits")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static long? ParseLimit(string limits)
    {
        const string label = "Max locked memory";
        foreach (string line in limits.Split('\n'))
        {
            if (!line.StartsWith(label, StringComparison.Ordinal)) continue;
            string[] fields = line[label.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[2] != "bytes") return null;
            if (fields[0] == "unlimited") return -1;
            return long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
                ? bytes : null;
        }
        return null;
    }

    internal static string? Note(long? limit, bool windowsPluginsAvailable)
        => windowsPluginsAvailable && limit is >= 0 and < RecommendedBytes
            ? "Windows plugins inherit a low memory-lock limit from the running daemon ("
                + (limit.Value / 1048576d).ToString("0.##", CultureInfo.InvariantCulture)
                + " MiB; recommended: at least 256 MiB). This can cause repeated yabridge warnings and audio dropouts. "
                + "Configure your session's audio memory-lock allowance, then sign out completely and back in. "
                + "Restarting OpenXLR alone does not update the session's allowance."
            : null;
}
