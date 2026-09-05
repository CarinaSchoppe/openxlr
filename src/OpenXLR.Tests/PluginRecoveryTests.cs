using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginRecoveryTests
{
    [Fact]
    public void BackoffThenQuarantineRequiresExplicitClear()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
        var tracker = new PluginRecoveryTracker(() => now);

        tracker.RecordFailure("xlr1", "compressor", "crash 1");
        Assert.False(tracker.CanAttempt("xlr1", "compressor"));
        Assert.Equal(now.AddSeconds(2), tracker.Get("xlr1", "compressor")!.RetryAt);

        now = now.AddSeconds(2);
        Assert.True(tracker.CanAttempt("xlr1", "compressor"));
        tracker.RecordFailure("xlr1", "compressor", "crash 2");
        now = now.AddSeconds(4);
        tracker.RecordFailure("xlr1", "compressor", "crash 3");

        PluginRecoveryStatus quarantined = tracker.Get("xlr1", "compressor")!;
        Assert.True(quarantined.Quarantined);
        Assert.Null(quarantined.RetryAt);
        Assert.False(tracker.Retry("xlr1", "compressor", clearQuarantine: false));
        Assert.True(tracker.Retry("xlr1", "compressor", clearQuarantine: true));
        Assert.True(tracker.CanAttempt("xlr1", "compressor"));
    }

    [Fact]
    public void StableRecoveryAgesOutAndRemovedInsertIsForgotten()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
        var tracker = new PluginRecoveryTracker(() => now);
        tracker.RecordFailure("mix:stream", "eq", "killed");
        now = now.AddSeconds(2);
        tracker.MarkHealthy("mix:stream", "eq");

        now = now.Add(PluginRecoveryTracker.StabilityWindow);
        Assert.Null(tracker.Get("mix:stream", "eq"));

        tracker.RecordFailure("mix:stream", "limiter", "hung");
        tracker.Retain("mix:stream", ["eq"]);
        Assert.Null(tracker.Get("mix:stream", "limiter"));
    }
}
