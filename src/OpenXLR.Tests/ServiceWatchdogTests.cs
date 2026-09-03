using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class ServiceWatchdogTests
{
    [Theory]
    [InlineData("60000000", null, 42, 60)]
    [InlineData("60000000", "42", 42, 60)]
    [InlineData("60000000", "43", 42, 0)]
    [InlineData("60000000", "bad", 42, 0)]
    [InlineData(null, null, 42, 0)]
    [InlineData("-1", null, 42, 0)]
    [InlineData("0", null, 42, 0)]
    [InlineData("9223372036854775807", null, 42, 0)]
    public void WatchdogHonoursSystemdIntervalAndProcessOwnership(string? interval, string? pid, int current, int seconds)
        => Assert.Equal(seconds, ServiceWatchdog.WatchdogInterval(interval, pid, current)?.TotalSeconds ?? 0);

    [Fact]
    public void HungWorkerExpiresButCompletedFailedPollCanRecover()
    {
        var clock = new ManualClock();
        var progress = new ServiceProgress(clock);
        Assert.True(progress.IsRecent(TimeSpan.FromSeconds(30)));
        clock.Now = TimeSpan.FromSeconds(30).Ticks;
        Assert.False(progress.IsRecent(TimeSpan.FromSeconds(30)));
        progress.Mark();
        Assert.True(progress.IsRecent(TimeSpan.FromSeconds(30)));
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Now { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
    }
}
