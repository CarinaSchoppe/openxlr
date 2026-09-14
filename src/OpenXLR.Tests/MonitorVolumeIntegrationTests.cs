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
        // The graph dump is cached for a fraction of a second, so ask for the
        // node until the snapshot the adapter holds is one that has it.
        int node = 0;
        Assert.True(SpinWait.SpinUntil(() => (node = pw.FindNodeId("test_headphones") ?? 0) != 0, TimeSpan.FromSeconds(3)));
        command = ProcessRunner.Run("wpctl", ["set-volume", node.ToString(), "0.3"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.3, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.3, pw.GetSinkVolume("test_speakers"));

        // Desktop applets and media keys go past unity. What is read there
        // has to be what the other selected outputs are set to, or they sit
        // quieter with the mixer believing both are at 120%.
        command = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", "120%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(1.2, mixer.Snapshot().OutputVolume);
        Assert.Equal(1.2, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());

        mixer.SetOutputVolume(0.58);
        Assert.Equal(0.58, pw.GetSinkVolume("test_headphones"));
        Assert.Equal(0.58, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());

        // Relinking the same devices must not swallow the next desktop
        // adjustment before the daemon's next volume poll.
        mixer.SetMonitorOutputs(["test_headphones", "test_speakers"]);
        command = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", "52%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.52, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.52, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());

        pw.SetSinkVolume("test_speakers", 0.31);
        mixer.SetMonitorOutputs(["test_speakers", "test_headphones"]);
        mixer.EnforceDefaults();
        Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "test_speakers", TimeSpan.FromSeconds(3)));
        mixer.SyncDeviceVolumes();
        Assert.Equal(0.31, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.52, pw.GetSinkVolume("test_headphones"));

        mixer.SetMonitorOutputs([]);
        Assert.False(mixer.EnforceDefaults());
        Assert.Equal("test_speakers", pw.GetDefaultSink());
        mixer.SyncDeviceVolumes();
        Assert.Null(mixer.Snapshot().OutputVolume);
    }

    [MonitorPipeWireFact]
    public void AnOutputThatRefusedTheVolumeIsPutRightWhenItComesBack()
    {
        var pw = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("retry_first", "Retry first");
        uint second = pw.CreateNullSink("retry_second", "Retry second");
        mixer.Build(new MixerConfig { Channels = [], Mixes = [] });
        pw.SetSinkVolume("retry_first", 0.6);
        pw.SetSinkVolume("retry_second", 0.6);
        mixer.SetMonitorOutputs(["retry_first", "retry_second"]);
        mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.6, mixer.Snapshot().OutputVolume);

        // The second device leaves (suspend, unplug, a profile change) and
        // the desktop turns the first one down while it is away.
        pw.UnloadModule(second);
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("retry_second") is null, TimeSpan.FromSeconds(3)));
        ProcessResult command = ProcessRunner.Run("pactl", ["set-sink-volume", "retry_first", "20%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.2, mixer.Snapshot().OutputVolume);

        // It comes back at the level it remembers, and nothing touches the
        // first output again: only what it was owed can put it right.
        pw.CreateNullSink("retry_second", "Retry second");
        pw.SetSinkVolume("retry_second", 0.6);
        Assert.False(mixer.SyncDeviceVolumes());
        Assert.Equal(0.2, pw.GetSinkVolume("retry_second"));
        Assert.Equal(0.2, mixer.Snapshot().OutputVolume);

        // And once delivered it is left alone, desktop volume and all.
        pw.SetSinkVolume("retry_second", 0.7);
        Assert.False(mixer.SyncDeviceVolumes());
        Assert.Equal(0.7, pw.GetSinkVolume("retry_second"));
    }

    [MonitorPipeWireFact]
    public void ANewSelectionDropsWhatTheOldOneStillOwed()
    {
        var pw = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("stale_first", "Stale first");
        uint second = pw.CreateNullSink("stale_second", "Stale second");
        pw.CreateNullSink("stale_third", "Stale third");
        mixer.Build(new MixerConfig { Channels = [], Mixes = [] });
        pw.SetSinkVolume("stale_first", 0.6);
        pw.SetSinkVolume("stale_second", 0.6);
        pw.SetSinkVolume("stale_third", 0.9);
        mixer.SetMonitorOutputs(["stale_first", "stale_second"]);
        mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
        Assert.True(mixer.SyncDeviceVolumes());

        pw.UnloadModule(second);
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("stale_second") is null, TimeSpan.FromSeconds(3)));
        ProcessResult command = ProcessRunner.Run("pactl", ["set-sink-volume", "stale_first", "20%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        pw.CreateNullSink("stale_second", "Stale second");
        pw.SetSinkVolume("stale_second", 0.6);

        // Another device leads the selection now, so its own level is the
        // baseline and the level owed to the old one is not written anywhere.
        mixer.SetMonitorOutputs(["stale_third", "stale_second"]);
        mixer.SyncDeviceVolumes();
        Assert.Equal(0.9, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.6, pw.GetSinkVolume("stale_second"));
        Assert.False(mixer.SyncDeviceVolumes());
        Assert.Equal(0.6, pw.GetSinkVolume("stale_second"));
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
