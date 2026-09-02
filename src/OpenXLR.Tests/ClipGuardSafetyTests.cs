using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class ClipGuardSafetyTests
{
    [Fact]
    public void FindsLimiterInConfiguredSearchDirectory()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-ladspa-").FullName;
        try
        {
            string plugin = Path.Combine(directory, "hard_limiter_1413.so");
            File.WriteAllBytes(plugin, []);

            Assert.Equal(plugin,
                PipeWireAdapter.FindLadspaPlugin("hard_limiter_1413.so", [directory]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingLimiterIsReportedWithoutChangingRequestedState()
    {
        const string error = "install swh-plugins and restart OpenXLR";
        var adapter = new PipeWireAdapter(() => new DspFeatureAvailability(false, error));
        var mixer = new Mixer(adapter);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => mixer.SetSoftClipGuard(true));

        Assert.Contains("swh-plugins", ex.Message);
        Assert.False(mixer.SoftClipGuard);
        Assert.False(mixer.Snapshot().SoftClipGuardAvailable);
    }
}
