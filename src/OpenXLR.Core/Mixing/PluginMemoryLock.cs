using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>The running daemon's memory-lock allowance, inherited by its plugin hosts.</summary>
internal static class PluginMemoryLock
{
    // Match yabridge's memlock_min_safe_threshold in src/plugin/bridges/common.h.
    internal const long RecommendedBytes = 256L * 1024 * 1024;

    /// <summary>Bytes, -1 for unlimited, or null when the limit cannot be read.</summary>
    internal static (long? Soft, long? Hard) ReadLimits()
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        try { return ParseLimits(File.ReadAllText("/proc/self/limits")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    internal static (long? Soft, long? Hard) ParseLimits(string limits)
    {
        const string label = "Max locked memory";
        foreach (string line in limits.Split('\n'))
        {
            if (!line.StartsWith(label, StringComparison.Ordinal)) continue;
            string[] fields = line[label.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[2] != "bytes") return (null, null);
            return (Bytes(fields[0]), Bytes(fields[1]));
        }
        return (null, null);
    }

    private static long? Bytes(string field) => field == "unlimited" ? -1
        : long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : null;

    internal static string? Note(long? limit, long? hardLimit, bool windowsPluginsAvailable)
    {
        if (!windowsPluginsAvailable || limit is not (>= 0 and < RecommendedBytes)) return null;
        string advice = hardLimit switch
        {
            -1 or >= RecommendedBytes => "The hard limit has enough room. Set LimitMEMLOCK=infinity in the daemon's user unit and restart the daemon to raise its soft limit.",
            >= 0 => "The daemon's hard limit is also below 256 MiB. Check the user manager's memory-lock ceiling: a user unit cannot exceed it. If that ceiling is low, configure the session's PAM or systemd allowance, then sign out completely and back in. Restarting OpenXLR alone cannot raise a session ceiling.",
            _ => "The hard limit could not be read. Check the user manager's memory-lock ceiling before choosing between a unit setting and a session allowance change.",
        };
        return "Windows plugins inherit a low memory-lock limit from the running daemon ("
            + (limit.Value / 1048576d).ToString("0.##", CultureInfo.InvariantCulture)
            + " MiB; recommended: at least 256 MiB). This can cause repeated yabridge warnings and audio dropouts. " + advice;
    }
}
