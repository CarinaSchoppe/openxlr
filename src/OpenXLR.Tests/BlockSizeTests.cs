using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class BlockSizeTests
{
    [Fact]
    public void AForcedBlockSizeRidesOnEverySinkMeter()
    {
        using var meters = new MeterReader();
        Assert.DoesNotContain(meters.SinkMeter("OpenXLR_mix_monitor").ArgumentList, a => a.Contains("force-quantum"));
        meters.SetForceQuantum(256);
        Assert.Equal(256, meters.ForceQuantum);
        Assert.Contains("--property=node.force-quantum=256", meters.SinkMeter("OpenXLR_mix_monitor").ArgumentList);
    }

    [Fact]
    public void ClearingAForceHoldsTheDefaultQuantumBeforeLettingGo()
    {
        // A device in IRQ mode keeps a forced period after the force goes, so
        // the meters force PipeWire's default for a moment first.
        using var meters = new MeterReader { ReleaseHold = TimeSpan.FromMilliseconds(100) };
        meters.SetForceQuantum(512);
        meters.SetForceQuantum(null);
        int? held = meters.ForceQuantum;
        Assert.NotNull(held);
        Assert.NotEqual(512, held);
        Assert.True(SpinWait.SpinUntil(() => meters.ForceQuantum is null, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ANewForceDuringTheReleaseWins()
    {
        using var meters = new MeterReader { ReleaseHold = TimeSpan.FromMilliseconds(100) };
        meters.SetForceQuantum(512);
        meters.SetForceQuantum(null);
        meters.SetForceQuantum(256);
        Thread.Sleep(300);
        Assert.Equal(256, meters.ForceQuantum);
    }

    [Fact]
    public void NothingIsForcedWhenNoneWasChosen()
    {
        using var meters = new MeterReader { ReleaseHold = TimeSpan.FromMilliseconds(10) };
        meters.SetForceQuantum(null);
        Assert.Null(meters.ForceQuantum);
    }

    [Fact]
    public void TheMixerOffersOnlyTheListedSizesAndSavesTheChoice()
    {
        using var mixer = new Mixer();
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetBlockSize(300));
        Assert.Null(mixer.BlockSize);
        string? written = null;
        mixer.SetBlockSize(256, s => { written = s.BlockSize?.ToString(); return null; });
        Assert.Equal("256", written);
        Assert.Equal(256, mixer.Snapshot().BlockSize);
        Assert.Equal([256, 512, 1024], mixer.Snapshot().BlockSizes);
        Assert.Equal(256, mixer.ExportSettings().BlockSize);
    }

    [Fact]
    public void AFailedSaveKeepsTheOldBlockSize()
    {
        using var mixer = new Mixer();
        mixer.SetBlockSize(512);
        Assert.Throws<IOException>(() => mixer.SetBlockSize(256, _ => "disk full"));
        Assert.Equal(512, mixer.BlockSize);
    }

    [Fact]
    public void SavedBlockSizesOutsideTheListAreDropped()
    {
        string folder = Path.Combine(Path.GetTempPath(), "openxlr-block-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "mixer.json");
            File.WriteAllText(path, """{"blockSize":300}""");
            MixerSettings? loaded = MixerSettings.Load(path, out string? warning);
            Assert.Null(loaded!.BlockSize);
            Assert.Contains("blockSize", warning);
            File.WriteAllText(path, """{"blockSize":512}""");
            Assert.Equal(512, MixerSettings.Load(path, out _)!.BlockSize);
            File.WriteAllText(path, "{}");
            Assert.Null(MixerSettings.Load(path, out _)!.BlockSize);
            using var restored = new Mixer();
            restored.ApplySettings(new MixerSettings { BlockSize = 1024 });
            Assert.Equal(1024, restored.BlockSize);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("256", true)]
    [InlineData("512", true)]
    [InlineData("1024", true)]
    [InlineData("300", false)]
    [InlineData("2048", false)]
    [InlineData("256.5", false)]
    [InlineData("\"256\"", false)]
    [InlineData("true", false)]
    public void TheCommandAcceptsNullOrAListedSize(string value, bool accepted)
    {
        using JsonDocument doc = JsonDocument.Parse(value);
        var command = new Command { Cmd = "setBlockSize", Value = doc.RootElement.Clone() };
        using var mixer = new Mixer();
        Assert.Equal(accepted, CommandValidation.Check(command, mixer, _ => null) is null);
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "setBlockSize" }, mixer, _ => null));
    }
}
