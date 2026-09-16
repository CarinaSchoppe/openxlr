using System.Globalization;
using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorVolumeSyncTests
{
    private sealed class Sinks : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-sinks-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        public PipeWireAdapter Adapter { get; } = new();
        public Mixer Mixer { get; }
        public string[] Writes => File.ReadAllLines(Path.Combine(_dir, "writes"));
        public int Dumps => File.ReadAllLines(Path.Combine(_dir, "reads")).Length;

        public Sinks()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), """
                dir=${0%/*}
                echo read >> "$dir/reads"
                /bin/cat "$dir/dump.json"
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                """);
            Environment.SetEnvironmentVariable("PATH", _dir);
            Adapter.SetSinkMuted("OpenXLR_mix_monitor", false);
            File.WriteAllText(Path.Combine(_dir, "writes"), "");
            Mixer = new(Adapter);
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        }

        public void Dump(double volume, bool muted = false, bool internalSink = false)
        {
            string amplitude = Math.Pow(volume, 3).ToString("R", CultureInfo.InvariantCulture);
            string monitor = $$$$"""
                {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_mix_monitor","media.class":"Audio/Sink"},
                "params":{"Props":[{"channelVolumes":[{{{{amplitude}}}}],"mute":{{{{JsonSerializer.Serialize(muted)}}}}}]}}}
                """;
            string channel = """
                {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_ch_system","media.class":"Audio/Sink"},
                "params":{"Props":[{"channelVolumes":[0.125],"mute":true}]}}}
                """;
            File.WriteAllText(Path.Combine(_dir, "dump.json"), "[" + monitor + (internalSink ? "," + channel : "") + "]");
        }

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Directory.Delete(_dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(0.665, 0.66)]
    [InlineData(0.675, 0.68)]
    [InlineData(1.205, 1.2)]
    public void RoundedReadbackDoesNotChangeTheStoredMaster(double stored, double readback)
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMixVolume("monitor", stored);
        sinks.Dump(readback);

        Assert.False(sinks.Mixer.SyncMonitorVolumes());

        Assert.Equal(stored, Assert.Single(sinks.Mixer.Snapshot().Mixes).Volume);
    }

    [Theory]
    [InlineData(0.65, 0.65)]
    [InlineData(1.2, 1.2)]
    [InlineData(1.6, 1.5)]
    public void DesktopChangesAreReadAndBoostIsClamped(double desktop, double expected)
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMixVolume("monitor", 0.665);
        sinks.Dump(desktop, muted: true);

        Assert.True(sinks.Mixer.SyncMonitorVolumes());

        var mix = Assert.Single(sinks.Mixer.Snapshot().Mixes);
        Assert.Equal(expected, mix.Volume);
        Assert.True(mix.Muted);
        if (desktop > 1.5) Assert.Contains("set-sink-volume OpenXLR_mix_monitor 150%", sinks.Writes);
    }

    [Fact]
    public void SinkRepairAndMonitorReadbackShareOneDump()
    {
        using var sinks = new Sinks();
        sinks.Dump(0.6, internalSink: true);

        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out var restored));

        Assert.Equal(["OpenXLR_ch_system"], restored);
        Assert.Equal(0.6, Assert.Single(sinks.Mixer.Snapshot().Mixes).Volume);
        Assert.Equal(1, sinks.Dumps);
        Assert.Contains("set-sink-volume OpenXLR_ch_system 100%", sinks.Writes);
        Assert.Contains("set-sink-mute OpenXLR_ch_system 0", sinks.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUiWriteInvalidatesTheDumpBeforeTheNextSweep(bool mute)
    {
        using var sinks = new Sinks();
        sinks.Dump(0.6);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        if (mute) sinks.Mixer.SetMixMuted("monitor", true);
        else sinks.Mixer.SetMixVolume("monitor", 0.8);
        sinks.Dump(mute ? 0.6 : 0.8, muted: mute);

        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));

        Assert.Equal(2, sinks.Dumps);
        var mix = Assert.Single(sinks.Mixer.Snapshot().Mixes);
        Assert.Equal(mute ? 0.6 : 0.8, mix.Volume);
        Assert.Equal(mute, mix.Muted);
    }
}
