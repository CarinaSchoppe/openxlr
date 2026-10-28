using OpenXLR.Tui;

namespace OpenXLR.Tests;

public sealed class TuiNoticeTimingTests
{
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void ClockCorrectionsCannotExpireOrExtendANotice(int seconds)
    {
        var clock = new Clock();
        var app = new App(new DaemonLink(), Theme.Material, time: clock);
        app.Say("temporary notice");
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Offset = TimeSpan.FromSeconds(seconds);
        Assert.Contains("temporary notice", Bottom(app), StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("temporary notice", Bottom(app), StringComparison.Ordinal);
    }

    [Fact]
    public void AReplacementNoticeGetsItsOwnFourSeconds()
    {
        var clock = new Clock();
        var app = new App(new DaemonLink(), Theme.Material, time: clock);
        app.Say("first notice");
        clock.Advance(TimeSpan.FromSeconds(3));
        app.Say("second notice");
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Contains("second notice", Bottom(app), StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("second notice", Bottom(app), StringComparison.Ordinal);
    }

    private static string Bottom(App app)
    {
        Screen screen = new(120, 32);
        app.Draw(screen);
        return new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, screen.Height - 1).Ch).ToArray());
    }

    internal sealed class Clock : TimeProvider
    {
        private readonly ManualTime _elapsed = new();
        public TimeSpan Offset { get; set; }
        public override long TimestampFrequency => _elapsed.TimestampFrequency;
        public override long GetTimestamp() => _elapsed.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _elapsed.GetUtcNow() + Offset;
        public void Advance(TimeSpan elapsed) => _elapsed.Advance(elapsed);
    }
}
