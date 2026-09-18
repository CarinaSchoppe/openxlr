using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void OutputKeysFollowTheCurrentDefaultAndKeepMonitorMastersIndependent()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [new("system", "System")], Mixes =
            [new("monitor", "Monitor A", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor)] });
        pw.CreateNullSink("test_key_headset", "Key headset");
        pw.CreateNullSink("test_key_speakers", "Key speakers");
        pw.CreateVirtualMic("test_key_mic", "test_key_headset.monitor", "Key microphone");
        Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Count(d => d.Name.StartsWith("test_key_")) == 3, TimeSpan.FromSeconds(3)));
        mixer.SetEnforcedDefaults(null, "test_key_mic");
        mixer.SetMainOutput("test_key_headset");
        pw.SetSinkVolume("test_key_headset", .98);
        pw.SetSinkVolume("test_key_speakers", .45);
        mixer.AdjustOutputVolume(null, .05);
        Assert.Equal(1.03, pw.GetSinkVolume("test_key_headset"));
        Assert.Equal(.45, pw.GetSinkVolume("test_key_speakers"));
        mixer.AdjustOutputVolume(null, .5);
        Assert.Equal(1.5, pw.GetSinkVolume("test_key_headset"));
        mixer.AdjustOutputVolume(null, .5);
        Assert.Equal(1.5, pw.GetSinkVolume("test_key_headset"));
        pw.SetSinkVolume("test_key_headset", .01);
        mixer.AdjustOutputVolume(null, -.05);
        Assert.Equal(0, pw.GetSinkVolume("test_key_headset"));
        mixer.ToggleOutputMute(null);
        Assert.Contains("yes", ProcessRunner.Run("pactl", ["get-sink-mute", "test_key_headset"]).StdoutText);
        mixer.ToggleOutputMute(null);
        Assert.Contains("no", ProcessRunner.Run("pactl", ["get-sink-mute", "test_key_headset"]).StdoutText);
        mixer.SetMainOutput("test_key_speakers");
        Assert.Equal("test_key_mic", mixer.ExportSettings().EnforcedDefaultSource);
        Assert.Equal("test_key_mic", pw.GetDefaultSource());
        mixer.AdjustOutputVolume(null, -.05);
        Assert.Equal(.4, pw.GetSinkVolume("test_key_speakers"));
        Assert.Equal(0, pw.GetSinkVolume("test_key_headset"));
        mixer.SetMainOutput("OpenXLR_mix_monitor2");
        mixer.AdjustOutputVolume(null, .2);
        mixer.ToggleOutputMute(null);
        var state = mixer.Snapshot();
        Assert.Equal(1.2, state.Mixes.Single(m => m.Id == "monitor2").Volume);
        Assert.True(state.Mixes.Single(m => m.Id == "monitor2").Muted);
        Assert.Equal(1, state.Mixes.Single(m => m.Id == "monitor").Volume);
        Assert.False(state.Mixes.Single(m => m.Id == "monitor").Muted);
        mixer.ToggleOutputMute(null);
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
        Assert.False(pw.GetSinkMuted("OpenXLR_mix_monitor2"));
        foreach (string bad in new[] { "missing", "@DEFAULT_SINK@", "123", "test_key_headset#hp1", "OpenXLR_ch_system" })
            Assert.Throws<InvalidOperationException>(() => mixer.SetMainOutput(bad));
        Assert.Equal("OpenXLR_mix_monitor2", mixer.ExportSettings().EnforcedDefaultSink);
        Assert.Throws<InvalidOperationException>(() => mixer.AdjustOutputVolume(null, double.NaN));
        mixer.SetMonitorOutputs(["test_key_headset", "test_key_speakers"]);
        mixer.SetMainOutput(Mixer.FollowMonitorOutput);
        mixer.AdjustOutputVolume(null, .1);
        Assert.Equal(.1, mixer.Snapshot().OutputVolume);
        Assert.Equal(.1, pw.GetSinkVolume("test_key_speakers"));
        Assert.Equal(Mixer.FollowMonitorOutput, mixer.ExportSettings().EnforcedDefaultSink);
        Assert.Equal("test_key_mic", mixer.ExportSettings().EnforcedDefaultSource);
    }
}
