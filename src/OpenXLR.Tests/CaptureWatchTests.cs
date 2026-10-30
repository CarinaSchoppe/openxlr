using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// When the daemon decides a unit's capture has stalled under its own
/// playback. A reset must never come from a quiet room, a muted microphone
/// or a held note, and must not repeat in a storm.
/// </summary>
public sealed class CaptureWatchTests
{
    private const string Source = "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo";
    private const string Sink = "alsa_output.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo";
    private static readonly UnitPlayback Playing = new(Source, Sink);
    private static readonly CaptureRun Running = new(582, Alsa: true, NodeRunning: true, KernelRunning: true);
    private static readonly CaptureRun Stopped = new(582, Alsa: true, NodeRunning: true, KernelRunning: false);

    private static float[]? NoSample(string _) => throw new InvalidOperationException("no sample is taken while the kernel says Stop");

    [Fact]
    public void AKernelStopThatLastsTheWindowRecoversOnceAndThenWaits()
    {
        long now = 0;
        var watch = new CaptureWatch(() => now);
        var verdicts = new List<CaptureVerdict>();
        // Ten minutes of a stopped capture, one look a second.
        for (; now <= 10 * 60_000; now += 1000) verdicts.Add(watch.Observe(Playing, _ => Stopped, NoSample));
        List<CaptureVerdict> recoveries = [.. verdicts.Where(v => v.Recover)];
        // The first after the three second window, then one per spacing, and
        // no more than the cap within the half hour.
        Assert.Equal(CaptureWatch.MaxRecoveries, recoveries.Count);
        Assert.False(verdicts[2].Recover);
        Assert.True(verdicts[3].Recover);
        Assert.Contains("stopped for 3 seconds", recoveries[0].Reason, StringComparison.Ordinal);
        Assert.Single(verdicts, v => v.Note is not null);
        Assert.Contains("Unplug it", verdicts.Single(v => v.Note is not null).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AStopThatEndsOrAUnitNoLongerAtRiskStartsTheCountOver()
    {
        long now = 0;
        var watch = new CaptureWatch(() => now);
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 2; i++, now += 1000) Assert.False(watch.Observe(Playing, _ => Stopped, NoSample).Recover);
            // The monitor moved off the unit's headphones for a sweep.
            Assert.False(watch.Observe(null, _ => Stopped, NoSample).Recover);
            now += 1000;
        }
        // A capture PipeWire does not run is idle, not stalled.
        for (int i = 0; i < 10; i++, now += 1000)
            Assert.False(watch.Observe(Playing, _ => Stopped with { NodeRunning = false }, NoSample).Recover);
    }

    [Fact]
    public void AReplayedBlockRecoversAfterThreeSamplesInARow()
    {
        long now = 0;
        var watch = new CaptureWatch(() => now);
        int samples = 0;
        float[] Buzz(string source)
        {
            Assert.Equal(Source, source);
            samples++;
            return Signal(t => Block256(t));
        }
        var recovered = new List<long>();
        for (; now <= 60_000; now += 1000)
            if (watch.Observe(Playing, _ => Running, Buzz).Recover) recovered.Add(now);
        // Samples at 10, 20 and 30 seconds; the third decides.
        Assert.Equal([30_000L], recovered);
        Assert.Equal(5, samples);   // the count goes on after the recovery, and the spacing holds the next
    }

    [Theory]
    [InlineData("room")]
    [InlineData("silence")]
    [InlineData("tone")]
    [InlineData("tone on the block")]
    [InlineData("voice")]
    public void AMicrophoneSignalNeverCountsAsAStall(string kind)
    {
        var random = new Random(7);
        double Noise(double dbfs) => Math.Pow(10, dbfs / 20) * Gaussian(random);
        Func<int, double> signal = kind switch
        {
            "room" => _ => Noise(-75),
            // A muted unit records zeros.
            "silence" => _ => 0,
            "tone" => t => 0.5 * Math.Sin(2 * Math.PI * 1000 * t / 48000) + Noise(-75),
            // 750 Hz repeats every 256 samples; the capsule's noise does not.
            "tone on the block" => t => 0.9 * Math.Sin(2 * Math.PI * 750 * t / 48000) + Noise(-80),
            _ => t => 0.3 * Math.Sin(2 * Math.PI * (180 + 40 * Math.Sin(t / 4800.0)) * t / 48000) * (1 + Math.Sin(t / 2000.0)) + Noise(-70),
        };
        CaptureRepeat.Result result = CaptureRepeat.Measure(Signal(signal));
        Assert.False(result.Repeating, $"{kind}: {result}");

        long now = 0;
        var watch = new CaptureWatch(() => now);
        for (; now <= 10 * 60_000; now += 1000)
            Assert.False(watch.Observe(Playing, _ => Running, _ => Signal(signal)).Recover);
    }

    [Fact]
    public void TheMeasureFindsTheBlockAndIgnoresShortOrMissingSamples()
    {
        CaptureRepeat.Result buzz = CaptureRepeat.Measure(Signal(Block256));
        Assert.True(buzz.Repeating);
        Assert.Equal(0, buzz.Lag % 256);
        Assert.True(buzz.LevelDb > -40);
        Assert.False(CaptureRepeat.Measure(null).Repeating);
        Assert.False(CaptureRepeat.Measure(new float[2 * 4000]).Repeating);
    }

    [Fact]
    public void TheSampleIsReadRawFromTheSourceWithoutHoldingIt()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-pw-record-").FullName;
        try
        {
            string args = Path.Combine(directory, "args");
            // One frame, 1.0 and -2.0 as little-endian floats, and the exit
            // status pw-record 1.6 gives when it stops at its sample count.
            string recorder = ExecutableScript.Write(Path.Combine(directory, "pw-record"),
                $"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{args}'\nprintf '\\000\\000\\200\\077\\000\\000\\000\\300'\nexit 1\n");
            Assert.Equal([1.0f, -2.0f], CaptureWatch.Sample(Source, recorder, frames: 1)!);
            string[] passed = File.ReadAllLines(args);
            Assert.Equal(CaptureWatch.SampleArguments(Source, frames: 1), passed);
            Assert.Equal(Source, passed[1]);
            Assert.Contains("--raw", passed);
            Assert.Contains(passed, a => a.Contains("node.passive = true", StringComparison.Ordinal));
            Assert.Equal("-", passed[^1]);
            Assert.Contains(CaptureWatch.SampleFrames.ToString(System.Globalization.CultureInfo.InvariantCulture), CaptureWatch.SampleArguments(Source));

            // A short read is no sample.
            Assert.Null(CaptureWatch.Sample(Source, recorder, frames: 2));
            Assert.Null(CaptureWatch.Sample(Source, Path.Combine(directory, "missing")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // A stalled stream replaying one 256-frame buffer: harmonics of 187.5 Hz only.
    private static double Block256(int t)
    {
        int k = t % 256;
        return 0.2 * Math.Sin(2 * Math.PI * k / 256) + 0.1 * Math.Sin(2 * Math.PI * 3 * k / 256) + (k < 8 ? 0.3 : 0);
    }

    private static float[] Signal(Func<int, double> at)
    {
        var samples = new float[2 * CaptureWatch.SampleFrames];
        for (int t = 0; t < CaptureWatch.SampleFrames; t++) samples[2 * t] = samples[2 * t + 1] = (float)at(t);
        return samples;
    }

    private static double Gaussian(Random random)
        => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
