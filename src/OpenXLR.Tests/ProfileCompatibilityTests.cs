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
}
