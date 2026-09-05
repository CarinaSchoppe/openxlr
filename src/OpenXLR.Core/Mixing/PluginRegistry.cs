namespace OpenXLR.Core.Mixing;

/// <summary>
/// Process-local, immutable view of the plug-in catalogue. The daemon's
/// isolated scanner is the only production writer; mixer reads are lock-free
/// and never load third-party code into the daemon process.
/// </summary>
public static class PluginRegistry
{
    private static IReadOnlyList<PluginInfo> _plugins = Array.Empty<PluginInfo>();

    public static IReadOnlyList<PluginInfo> Plugins => Volatile.Read(ref _plugins);

    public static PluginInfo? Find(string kind, string id)
        => Plugins.FirstOrDefault(plugin =>
            plugin.ScanStatus == "ready" &&
            string.Equals(plugin.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(plugin.Plugin, id, StringComparison.Ordinal));

    public static PluginInfo? Find(InsertDefinition insert) => Find(insert.Kind, insert.Plugin);

    /// <summary>Publish one complete scan atomically; partial scans never leak to readers.</summary>
    public static void Replace(IEnumerable<PluginInfo> plugins)
    {
        PluginInfo[] snapshot = plugins
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plugin => plugin.Kind, StringComparer.Ordinal)
            .ThenBy(plugin => plugin.Plugin, StringComparer.Ordinal)
            .ToArray();
        Volatile.Write(ref _plugins, snapshot);
    }
}
