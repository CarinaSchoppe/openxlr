using TerminalProgram = OpenXLR.Tui.Program;

namespace OpenXLR.Tests;

public sealed class TuiFrameTimingTests
{
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void ClockCorrectionsDoNotFreezeOrAccelerateRedraws(int seconds)
    {
        var clock = new Clock();
        long? previous = null;
        Assert.True(TerminalProgram.FrameDue(clock, ref previous));
        clock.Advance(20);
        clock.Wall = clock.Wall.AddSeconds(seconds);
        Assert.False(TerminalProgram.FrameDue(clock, ref previous));
        clock.Advance(46);
        Assert.True(TerminalProgram.FrameDue(clock, ref previous));
        Assert.False(TerminalProgram.FrameDue(clock, ref previous));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(4294967296, 1000)]
    [InlineData(0, 1000000000)]
    [InlineData(4294967296, 1000000000)]
    public void TheFirstFrameIsImmediateAndTheFrameIntervalIsInclusive(long start, long frequency)
    {
        var clock = new Clock(frequency) { Timestamp = start };
        long? previous = null;
        Assert.True(TerminalProgram.FrameDue(clock, ref previous));
        Assert.False(TerminalProgram.FrameDue(clock, ref previous));
        clock.Advance(65);
        Assert.False(TerminalProgram.FrameDue(clock, ref previous));
        clock.Advance(1);
        Assert.True(TerminalProgram.FrameDue(clock, ref previous));
    }

    [Fact]
    public void ADelayedFrameDoesNotCauseACatchUpBurst()
    {
        var clock = new Clock();
        long? previous = null;
        Assert.True(TerminalProgram.FrameDue(clock, ref previous));
        clock.Advance(60000);
        Assert.True(TerminalProgram.FrameDue(clock, ref previous));
        Assert.False(TerminalProgram.FrameDue(clock, ref previous));
        clock.Advance(65);
        Assert.False(TerminalProgram.FrameDue(clock, ref previous));
        clock.Advance(1);
        Assert.True(TerminalProgram.FrameDue(clock, ref previous));
    }

    private sealed class Clock(long frequency = 1000) : TimeProvider
    {
        public long Timestamp { get; set; }
        public DateTimeOffset Wall { get; set; } = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => frequency;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Wall;
        public void Advance(long milliseconds)
        {
            Timestamp += milliseconds * TimestampFrequency / 1000;
            Wall = Wall.AddMilliseconds(milliseconds);
        }
    }
}
