using System.Diagnostics;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MeterSampleTests
{
    [Fact]
    public void InvalidSamplesDoNotPoisonEitherChannelOrTheFollowingFrames()
    {
        byte[] input = Bytes(float.NaN, 0.25f, float.PositiveInfinity, 0.5f,
            float.NegativeInfinity, -0.5f, 0.5f, float.NaN);
        var result = MeterReader.AccumulateFrames(input, input.Length);
        Assert.Equal(0.25, result.SumL);
        Assert.Equal(0.5625, result.SumR);
        Assert.Equal(4, result.Frames);
        Assert.Equal(0, result.Carry);
        byte[] loud = Bytes(float.MaxValue, -float.MaxValue);
        var extremes = MeterReader.AccumulateFrames(loud, loud.Length);
        Assert.True(double.IsFinite(extremes.SumL));
        Assert.Equal((double)float.MaxValue * float.MaxValue, extremes.SumL);
        Assert.Equal(extremes.SumL, extremes.SumR);
    }

    [Fact]
    public void MeterMessagesRemainSerializableAfterInvalidAudio()
    {
        string path = Path.Combine(Path.GetTempPath(), "openxlr-meter-" + Guid.NewGuid());
        File.WriteAllBytes(path, Bytes(float.NaN, 0.5f, float.PositiveInfinity, 0.5f));
        try
        {
            using var meters = new MeterReader();
            var process = new ProcessStartInfo("cat") { RedirectStandardOutput = true, RedirectStandardError = true };
            process.ArgumentList.Add(path);
            meters.Add("test", process);
            IReadOnlyDictionary<string, double[]>? snapshot = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                snapshot = meters.Read();
                return snapshot.TryGetValue("test", out var levels) && levels[1] > 0;
            }, TimeSpan.FromSeconds(5)));
            Assert.Equal(0, snapshot!["test"][0]);
            Assert.All(snapshot["test"], level => Assert.InRange(level, 0, 1));
            Assert.Contains("test", JsonSerializer.Serialize(snapshot));
        }
        finally { File.Delete(path); }
    }

    private static byte[] Bytes(params float[] samples)
        => samples.SelectMany(BitConverter.GetBytes).ToArray();
}
