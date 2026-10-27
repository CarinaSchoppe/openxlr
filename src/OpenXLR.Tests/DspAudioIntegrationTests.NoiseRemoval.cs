using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Noise removal on a private PipeWire server, on the fake unit's 1 kHz
/// tone: switched on with the EQ off, the microphone stage splices in at the
/// head of XLR 1 without restarting the chain behind it; the engine, the
/// strength and the EQ in the same stage all change live, with no new node or
/// helper process; the stage reports the engine heard and its delay; off, the
/// stage stays; a profile recall switches it live. At strength 0 every engine
/// passes the tone at its level, which is what the levels check.
/// </summary>
public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public Task NoiseRemovalSplicesInAheadOfTheEqAndEveryChangeIsLive() => OnTheUnitWithTone(compensate: false, (mixer, pw, runs) =>
    {
        mixer.SetInserts("xlr1", [Fixture("one", 100)]);
        ChainGraph xlr1 = SettledChain("xlr1");
        double flat = Level("xlr1");

        // A strength set while it is off is only stored: no stage runs.
        mixer.SetNoiseRemoval("xlr1", new(Strength: 0));
        Assert.Empty(Chain("xlr1", "OpenXLR_eq_").Nodes);
        Assert.Null(Noise(mixer).Active);

        // On, with the EQ off: the stage comes in at the head, and the chain
        // behind it keeps its nodes and every link but the one it is fed by.
        mixer.SetNoiseRemoval("xlr1", new(Enabled: true));
        ChainGraph stage = SettledChain("xlr1", "OpenXLR_eq_");
        Assert.Single(stage.Nodes);
        AssertSpliced(xlr1, SettledChain("xlr1"), stage);
        xlr1 = SettledChain("xlr1");
        AssertLevel("xlr1", flat);
        WaitForNoise(mixer, "rnnoise", 30);
        Assert.False(mixer.EqFor("xlr1").Enabled, "The EQ itself stays off.");

        // The engine, the strength and the EQ beside it change live.
        int before = Volatile.Read(ref runs.Count);
        if (mixer.NoiseEngines.Contains(MicNoise.DeepFilterNet))
        {
            mixer.SetNoiseRemoval("xlr1", new(Engine: MicNoise.DeepFilterNet));
            WaitForNoise(mixer, "deepfilternet", null);
            Assert.True(Noise(mixer).LatencyMs > 40, $"DeepFilterNet reports {Noise(mixer).LatencyMs} ms");
            AssertLevel("xlr1", flat, 0.5);
            mixer.SetNoiseRemoval("xlr1", new(Engine: MicNoise.RnNoise));
            WaitForNoise(mixer, "rnnoise", 30);
        }
        mixer.SetEqBand("xlr1", 2, new(Frequency: 1000, Gain: 6, Q: 1));
        mixer.SetEqEnabled("xlr1", true);
        AssertLevel("xlr1", flat + 6);
        Assert.Equal(before, Volatile.Read(ref runs.Count));
        Assert.Equal(stage, SettledChain("xlr1", "OpenXLR_eq_"));
        Assert.Equal(xlr1, SettledChain("xlr1"));

        // Off, both: bypassed inside the stage, which stays until the route
        // is next built.
        mixer.SetNoiseRemoval("xlr1", new(Enabled: false));
        mixer.SetEqEnabled("xlr1", false);
        AssertLevel("xlr1", flat);
        WaitForNoise(mixer, null, null);
        Assert.Equal(stage, SettledChain("xlr1", "OpenXLR_eq_"));

        // A recall that names noise removal switches it live; one without
        // leaves it alone; the saved settings carry it.
        MixerScene scene = mixer.ExportScene();
        mixer.ApplyScene(scene with { NoiseRemoval = new() { ["xlr1"] = new MicNoise { Enabled = true, Strength = 0 } } });
        WaitForNoise(mixer, "rnnoise", 30);
        mixer.ApplyScene(scene with { NoiseRemoval = null });
        Assert.True(mixer.NoiseFor("xlr1").Enabled);
        Assert.True(mixer.ExportSettings().NoiseRemoval["xlr1"].Enabled);
        AssertLevel("xlr1", flat);
        Assert.Equal(stage, SettledChain("xlr1", "OpenXLR_eq_"));
        Assert.Equal(xlr1, SettledChain("xlr1"));
    });

    private static MicNoiseState Noise(Mixer mixer, string channel = "xlr1")
        => mixer.Snapshot().Channels.Single(c => c.Id == channel).Noise!;

    /// <summary>Sweep until the stage reports <paramref name="active"/>, and, when given, that delay in ms.</summary>
    private static void WaitForNoise(Mixer mixer, string? active, double? latencyMs)
    {
        MicNoiseState? last = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            last = Noise(mixer);
            return last.Active == active && (latencyMs is null || last.LatencyMs == latencyMs) && last.Error is null;
        }, TimeSpan.FromSeconds(10)), $"noise removal reports {last?.Active ?? "none"} at {last?.LatencyMs} ms ({last?.Error}), expected {active ?? "none"}");
    }
}
