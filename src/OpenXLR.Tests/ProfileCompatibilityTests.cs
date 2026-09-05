using System.Text.Json;
using OpenXLR.Core;

namespace OpenXLR.Tests;

public sealed class ProfileCompatibilityTests
{
    [Fact]
    public void OldSceneWithoutMonitorOutputsPreservesLegacyMeaning()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>("{}")!;
        Assert.Null(scene.MonitorOutputs);
    }

    [Fact]
    public void ExplicitEmptyMonitorOutputsMeansDisconnectAll()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>(
            """{"MonitorOutputs":[]}""")!;
        Assert.NotNull(scene.MonitorOutputs);
        Assert.Empty(scene.MonitorOutputs);
    }

    [Fact]
    public void OldSceneWithoutMonitoredMixPreservesCurrentSelection()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>("{}")!;
        Assert.Null(scene.MonitoredMixId);
    }

    [Fact]
    public void OldSceneWithoutRoutingMatrixPreservesCurrentRoutes()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>("{}")!;
        Assert.Null(scene.OutputRoutes);
    }

    [Fact]
    public void ExplicitEmptyRoutingMatrixDisconnectsAllRoutes()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>("""{"OutputRoutes":[]}""")!;
        Assert.NotNull(scene.OutputRoutes);
        Assert.Empty(scene.OutputRoutes);
    }

    [Fact]
    public void NewSceneRoundTripsMonitoredMix()
    {
        var original = new MixerScene { MonitoredMixId = "broadcast" };
        MixerScene restored = JsonSerializer.Deserialize<MixerScene>(JsonSerializer.Serialize(original))!;
        Assert.Equal("broadcast", restored.MonitoredMixId);
    }
}
