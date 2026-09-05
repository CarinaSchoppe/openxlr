using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// A named signal tap that can feed an output route. Values are persisted as
/// strings so adding a stage does not renumber settings written by an older
/// OpenXLR release.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessingStage>))]
public enum ProcessingStage
{
    Raw,
    HardwareFx,
    PluginFx,
    FullFx,
    MixProcessed,
}

/// <summary>
/// One desired matrix cell. DestinationId is derived from durable PipeWire
/// device properties, never from a display label or transient object serial.
/// The label is presentation-only and lets the UI explain an unplugged route.
/// </summary>
public sealed record OutputRouteDefinition(
    string MixId,
    string DestinationId,
    ProcessingStage Stage = ProcessingStage.MixProcessed,
    string? DestinationLabel = null);

/// <summary>A destination column in the routing matrix.</summary>
public sealed record RoutingDestination(
    string Id,
    string Name,
    string? NodeName,
    bool Available,
    bool IsPhysical,
    bool Compatible,
    IReadOnlyList<ProcessingStage> Stages,
    string? CompatibilityError = null);

/// <summary>Live status of one desired route.</summary>
public sealed record OutputRouteStatus(
    string MixId,
    string DestinationId,
    ProcessingStage Stage,
    bool Active,
    bool Available,
    string? Error = null,
    int SourceLatencySamples = 0,
    int CompensationSamples = 0);

/// <summary>Pure calculation used to align routes that converge on one destination.</summary>
internal static class LatencyCompensation
{
    internal static IReadOnlyDictionary<string, int> Calculate(
        IEnumerable<(string Key, int LatencySamples)> routes)
    {
        var normalized = routes.Select(route => (route.Key,
            Latency: Math.Clamp(route.LatencySamples, 0, CompensationDelayHost.MaximumSamples)))
            .ToList();
        int maximum = normalized.Count == 0 ? 0 : normalized.Max(route => route.Latency);
        return normalized.ToDictionary(route => route.Key,
            route => maximum - route.Latency, StringComparer.Ordinal);
    }
}

/// <summary>
/// Pure route validation shared by the daemon, migrations, and tests. Graph
/// nodes use stable IDs. A destination beginning with "mix:" is an internal
/// graph endpoint and therefore participates in cycle detection; normal audio
/// devices are terminal nodes.
/// </summary>
public static class SignalRouting
{
    public static string? Validate(
        OutputRouteDefinition route,
        IReadOnlyCollection<string> mixIds,
        IReadOnlyCollection<RoutingDestination> destinations,
        IEnumerable<OutputRouteDefinition>? existing = null)
    {
        if (!mixIds.Contains(route.MixId, StringComparer.Ordinal))
            return $"unknown mix '{route.MixId}'";
        RoutingDestination? destination = destinations.FirstOrDefault(d => d.Id == route.DestinationId);
        if (destination is null)
            return $"unknown output destination '{route.DestinationId}'";
        if (!destination.Available || destination.NodeName is null)
            return $"output destination '{destination.Name}' is disconnected";
        if (!destination.Compatible)
            return destination.CompatibilityError ?? $"output destination '{destination.Name}' is incompatible";
        if (!destination.Stages.Contains(route.Stage))
            return $"processing stage '{route.Stage}' is unavailable for '{destination.Name}'";

        string source = $"mix:{route.MixId}";
        if (source == route.DestinationId)
            return "a mix cannot route into itself";
        var edges = (existing ?? [])
            .Where(r => r != route)
            .Select(r => ($"mix:{r.MixId}", r.DestinationId));
        return WouldCreateCycle(source, route.DestinationId, edges)
            ? "route would create an audio feedback cycle"
            : null;
    }

    public static bool WouldCreateCycle(
        string source,
        string destination,
        IEnumerable<(string Source, string Destination)> edges)
    {
        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string from, string to) in edges.Append((source, destination)))
        {
            if (!outgoing.TryGetValue(from, out List<string>? targets))
                outgoing[from] = targets = [];
            targets.Add(to);
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return outgoing.Keys.Any(Visit);

        bool Visit(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node))
            {
                visiting.Remove(node);
                return false;
            }
            if (outgoing.TryGetValue(node, out List<string>? targets))
                foreach (string target in targets)
                    if (Visit(target)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
    }

    /// <summary>
    /// Produce an opaque, API-safe ID from stable device properties. The
    /// caller supplies a canonical identity assembled from properties such as
    /// device.serial, api.alsa.path and the hardware port suffix.
    /// </summary>
    public static string StableDestinationId(string canonicalIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalIdentity);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));
        return $"output:{Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
