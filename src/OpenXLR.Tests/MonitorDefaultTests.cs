using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MonitorDefaultTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("OpenXLR_ch_system", "OpenXLR_ch_system")]
    [InlineData("alsa_output.fixed", "alsa_output.fixed")]
    public void FixedAndUnmanagedDefaultsKeepTheirMeaning(string? setting, string? expected)
        => Assert.Equal(expected, Mixer.ResolveDefaultSink(setting, ["alsa_output.monitor"]));

    [Fact]
    public void FollowModeResolvesTheCurrentFirstOutputAndDropsPseudoDeviceMarkers()
    {
        Assert.Equal("alsa_output.headset", Mixer.ResolveDefaultSink(Mixer.FollowMonitorOutput,
            ["alsa_output.headset#hp1", "alsa_output.speakers"]));
        Assert.Equal("alsa_output.speakers", Mixer.ResolveDefaultSink(Mixer.FollowMonitorOutput,
            ["alsa_output.speakers", "alsa_output.headset#hp2"]));
        Assert.Null(Mixer.ResolveDefaultSink(Mixer.FollowMonitorOutput, []));
    }

    [Fact]
    public void FollowModeSurvivesSettingsSerialization()
    {
        var settings = new MixerSettings { EnforcedDefaultSink = Mixer.FollowMonitorOutput };
        var saved = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<MixerSettings>(saved);
        Assert.Equal(Mixer.FollowMonitorOutput, loaded!.EnforcedDefaultSink);
        Assert.Equal("alsa_output.monitor", Mixer.ResolveDefaultSink(loaded.EnforcedDefaultSink, ["alsa_output.monitor"]));
    }
}
