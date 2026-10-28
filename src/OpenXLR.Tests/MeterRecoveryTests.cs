using System.Diagnostics;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MeterRecoveryTests
{
    [Fact]
    public void AHelperMissingAtStartupCanAppearLater()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-meter-missing-").FullName;
        long now = 0;
        try
        {
            string helper = Path.Combine(directory, "capture");
            string samples = Path.Combine(directory, "samples");
            File.WriteAllBytes(samples, [.. BitConverter.GetBytes(0.5f), .. BitConverter.GetBytes(0.25f)]);
            var start = Capture(helper, samples);
            using var meters = new MeterReader(() => now);
            meters.Add("channel", start);
            Assert.Equal([0.0, 0.0], meters.Read()["channel"]);
            ExecutableScript.Write(helper, "cat \"$1\"\nread -r unused");
            now = 999;
            Assert.Equal([0.0, 0.0], meters.Read()["channel"]);
            now = 1000;
            Assert.True(SpinWait.SpinUntil(() => meters.Read()["channel"][0] > 0, TimeSpan.FromSeconds(5)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedFailuresBackOffAndRetiredMetersNeverRestart(bool dispose)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-meter-backoff-").FullName;
        long now = 0;
        int clockReads = 0;
        long Clock()
        {
            long value = Interlocked.Read(ref now);
            Interlocked.Increment(ref clockReads);
            return value;
        }
        try
        {
            string helper = ExecutableScript.Write(Path.Combine(directory, "capture"), "printf x >> \"$1\"\nexit 1");
            string launches = Path.Combine(directory, "launches");
            using var meters = new MeterReader(Clock);
            meters.Add("channel", Capture(helper, launches));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref clockReads) >= 2, TimeSpan.FromSeconds(5)));
            long delay = 1000;
            for (int attempt = 2; attempt <= 8; attempt++)
            {
                Interlocked.Add(ref now, delay - 1);
                for (int poll = 0; poll < 1000; poll++) meters.Read();
                Assert.Equal(attempt - 1, new FileInfo(launches).Length);
                int before = Volatile.Read(ref clockReads);
                Interlocked.Increment(ref now);
                meters.Read();
                // The poll, launch and EOF each read the clock. EOF publishes
                // the next retry under the same gate the next poll takes.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref clockReads) >= before + 3, TimeSpan.FromSeconds(5)));
                delay = Math.Min(delay * 2, 30_000);
            }
            if (dispose) meters.Dispose();
            else meters.Remove("channel");
            Interlocked.Add(ref now, 60_000);
            Assert.Empty(meters.Read());
            Assert.Equal(8, new FileInfo(launches).Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static ProcessStartInfo Capture(string helper, params string[] args)
    {
        var start = new ProcessStartInfo(helper)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    [Fact]
    public void AFailedCaptureRecoversWithoutRebuildingItsChannel()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-meter-recovery-").FullName;
        try
        {
            string helper = ExecutableScript.Write(Path.Combine(directory, "capture"), """
                printf x >> "$1"
                if [ "$(wc -c < "$1")" -lt 2 ]; then exit 1; fi
                cat "$2"
                read -r unused
                """);
            string launches = Path.Combine(directory, "launches");
            string samples = Path.Combine(directory, "samples");
            File.WriteAllBytes(samples, [.. BitConverter.GetBytes(0.5f), .. BitConverter.GetBytes(0.25f)]);
            var start = new ProcessStartInfo(helper)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(launches);
            start.ArgumentList.Add(samples);
            using var meters = new MeterReader();
            meters.Add("channel", start);
            Assert.True(SpinWait.SpinUntil(() => meters.Read().TryGetValue("channel", out var level) && level[0] > 0,
                TimeSpan.FromSeconds(5)), "The capture exited once, but the channel's meter never recovered.");
            Assert.Equal("xx", File.ReadAllText(launches));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
