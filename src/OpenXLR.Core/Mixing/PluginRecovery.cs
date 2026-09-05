namespace OpenXLR.Core.Mixing;

/// <summary>Live failure/retry state for one isolated plug-in process.</summary>
public sealed record PluginRecoveryStatus(
    int FailureCount,
    string LastError,
    DateTimeOffset LastFailure,
    DateTimeOffset? RetryAt,
    bool Quarantined);

/// <summary>
/// Bounded retry policy for native plug-in hosts. A target/insert pair is
/// quarantined after three failures within five minutes. Successful hosts
/// retain their recent failure count long enough to catch crash loops, then
/// age out automatically after five stable minutes.
/// </summary>
internal sealed class PluginRecoveryTracker
{
    internal const int QuarantineThreshold = 3;
    internal static readonly TimeSpan StabilityWindow = TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal PluginRecoveryTracker(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    internal static string Key(string target, string insertId) => $"{target}\0{insertId}";

    internal void RecordFailure(string target, string insertId, string error)
    {
        DateTimeOffset now = _clock();
        string key = Key(target, insertId);
        Entry entry = _entries.GetValueOrDefault(key) ?? new Entry();
        if (now - entry.LastFailure > StabilityWindow) entry.FailureCount = 0;
        entry.FailureCount++;
        entry.LastError = error;
        entry.LastFailure = now;
        entry.HealthySince = null;
        entry.Quarantined = entry.FailureCount >= QuarantineThreshold;
        entry.RetryAt = entry.Quarantined
            ? null
            : now + TimeSpan.FromSeconds(Math.Min(60, 1 << entry.FailureCount));
        _entries[key] = entry;
    }

    internal void MarkHealthy(string target, string insertId)
    {
        if (!_entries.TryGetValue(Key(target, insertId), out Entry? entry)) return;
        entry.RetryAt = null;
        entry.HealthySince ??= _clock();
    }

    internal bool CanAttempt(string target, string insertId)
    {
        PruneStable();
        return !_entries.TryGetValue(Key(target, insertId), out Entry? entry)
            || !entry.Quarantined && (entry.RetryAt is null || entry.RetryAt <= _clock());
    }

    internal bool IsRetryDue(string target)
    {
        PruneStable();
        DateTimeOffset now = _clock();
        string prefix = target + '\0';
        return _entries.Any(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
            && !pair.Value.Quarantined && pair.Value.RetryAt is not null
            && pair.Value.RetryAt <= now);
    }

    internal bool Retry(string target, string insertId, bool clearQuarantine)
    {
        if (!_entries.TryGetValue(Key(target, insertId), out Entry? entry)) return false;
        if (entry.Quarantined && !clearQuarantine) return false;
        entry.Quarantined = false;
        entry.RetryAt = _clock();
        entry.HealthySince = null;
        return true;
    }

    internal PluginRecoveryStatus? Get(string target, string insertId)
    {
        PruneStable();
        return !_entries.TryGetValue(Key(target, insertId), out Entry? entry) ? null
            : new PluginRecoveryStatus(entry.FailureCount, entry.LastError,
                entry.LastFailure, entry.RetryAt, entry.Quarantined);
    }

    internal void Retain(string target, IEnumerable<string> insertIds)
    {
        var keep = insertIds.Select(id => Key(target, id)).ToHashSet(StringComparer.Ordinal);
        string prefix = target + '\0';
        foreach (string key in _entries.Keys.Where(key =>
                     key.StartsWith(prefix, StringComparison.Ordinal) && !keep.Contains(key)).ToList())
            _entries.Remove(key);
    }

    private void PruneStable()
    {
        DateTimeOffset now = _clock();
        foreach (string key in _entries.Where(pair => pair.Value.HealthySince is { } since
                     && now - since >= StabilityWindow).Select(pair => pair.Key).ToList())
            _entries.Remove(key);
    }

    private sealed class Entry
    {
        internal int FailureCount;
        internal string LastError = "";
        internal DateTimeOffset LastFailure;
        internal DateTimeOffset? RetryAt;
        internal DateTimeOffset? HealthySince;
        internal bool Quarantined;
    }
}
