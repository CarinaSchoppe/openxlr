using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class AuditFixesTests
{
    // Frames split at every possible byte boundary must sum to the same
    // energy as one aligned read; a dropped remainder would shift every
    // later sample by a few bytes and turn the meters into noise.
    [Fact]
    public void MeterFramesSurviveFragmentedReads()
    {
        var frames = new float[] { 0.5f, -0.5f, 0.25f, 0.75f, 1f, 0f, -1f, 0.125f };
        byte[] stream = new byte[frames.Length * 4];
        Buffer.BlockCopy(frames, 0, stream, 0, stream.Length);
        (double wantL, double wantR, int wantFrames, int wantCarry) = MeterReader.AccumulateFrames((byte[])stream.Clone(), stream.Length);
        Assert.Equal(4, wantFrames);
        Assert.Equal(0, wantCarry);

        for (int split = 1; split < stream.Length; split++)
        {
            var buf = new byte[64];
            Buffer.BlockCopy(stream, 0, buf, 0, split);
            (double l1, double r1, int f1, int carry) = MeterReader.AccumulateFrames(buf, split);
            Buffer.BlockCopy(stream, split, buf, carry, stream.Length - split);
            (double l2, double r2, int f2, int rest) = MeterReader.AccumulateFrames(buf, carry + stream.Length - split);
            Assert.Equal(wantFrames, f1 + f2);
            Assert.Equal(0, rest);
            Assert.Equal(wantL, l1 + l2, 9);
            Assert.Equal(wantR, r1 + r2, 9);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("http://app.localhost", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://192.168.1.10:37890", false)]
    [InlineData("not a url", false)]
    [InlineData("null", false)]
    public void WebSocketOriginPolicy(string? origin, bool allowed)
        => Assert.Equal(allowed, LoopbackOrigin.IsAllowed(origin));

    [Fact]
    public void MonitorOverrideWinsOverSavedList()
    {
        var saved = new MixerSettings { MonitorOutput = "old", MonitorOutputs = ["a", "b"] };
        MixerSettings overridden = saved.WithMonitorOverride("env-sink");
        Assert.Equal("env-sink", overridden.MonitorOutput);
        Assert.Equal(["env-sink"], overridden.MonitorOutputs);
        Assert.Same(saved, saved.WithMonitorOverride(null));
    }
}
