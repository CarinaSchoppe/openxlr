using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void DesktopAndMixerShareTheSelectedOutputVolume()
    {
        // The runner creates a private PipeWire server. Never build or unload
        // a graph against the user's desktop during a regular test run.
        Assert.Equal("1", Environment.GetEnvironmentVariable("OPENXLR_TEST_MONITOR_VOLUME"));
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        var pw = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_headphones", "Test headphones");
        pw.CreateNullSink("test_speakers", "Test speakers");
        mixer.Build(new MixerConfig { Channels = [], Mixes = [] });
        pw.SetSinkVolume("test_headphones", 0.67);
        pw.SetSinkVolume("test_speakers", 0.45);
        mixer.SetMonitorOutputs(["test_headphones", "test_speakers"]);
        mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
        Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "test_headphones", TimeSpan.FromSeconds(3)));
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.67, mixer.Snapshot().OutputVolume);

        // The same command desktop media-key handlers use, against the
        // default device rather than an OpenXLR API command.
        ProcessResult command = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", "40%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.4, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.4, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());
        int node = pw.FindNodeId("test_headphones")!.Value;
        command = ProcessRunner.Run("wpctl", ["set-volume", node.ToString(), "0.3"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.3, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.3, pw.GetSinkVolume("test_speakers"));

        mixer.SetOutputVolume(0.58);
        Assert.Equal(0.58, pw.GetSinkVolume("test_headphones"));
        Assert.Equal(0.58, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());

        pw.SetSinkVolume("test_speakers", 0.31);
        mixer.SetMonitorOutputs(["test_speakers", "test_headphones"]);
        mixer.EnforceDefaults();
        Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "test_speakers", TimeSpan.FromSeconds(3)));
        mixer.SyncDeviceVolumes();
        Assert.Equal(0.31, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.58, pw.GetSinkVolume("test_headphones"));

        mixer.SetMonitorOutputs([]);
        Assert.False(mixer.EnforceDefaults());
        Assert.Equal("test_speakers", pw.GetDefaultSink());
        mixer.SyncDeviceVolumes();
        Assert.Null(mixer.Snapshot().OutputVolume);
    }
}

internal sealed class MonitorPipeWireFactAttribute : FactAttribute
{
    public MonitorPipeWireFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_MONITOR_VOLUME") != "1")
        Skip = "Run tools/test-monitor-volume.py against a private PipeWire server.";
    }
}
