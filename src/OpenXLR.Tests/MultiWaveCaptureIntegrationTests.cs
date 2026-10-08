using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void SelectedMonoPortsFeedBothSidesAndNeverFallBackToAnotherPort()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_wave_capture", "Wave fixture");
        pw.CreateVirtualMic("test_wave_source", "test_wave_capture.monitor", "Wave source");
        Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Any(d => d.Name == "test_wave_source"), TimeSpan.FromSeconds(5)));
        mixer.Build(new MixerConfig { Channels = [new("system", "System")], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        mixer.CreateCaptureChannel("Left", "test_wave_source", 0, _ => null, monoChannel: 0);
        mixer.CreateCaptureChannel("Right", "test_wave_source", 0, _ => null, monoChannel: 1);
        mixer.CreateCaptureChannel("Missing", "test_wave_source", 0, _ => null, monoChannel: 63);
        Assert.True(SpinWait.SpinUntil(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Count(c => c.CaptureConnected) == 2; }, TimeSpan.FromSeconds(5)));
        using var graph = JsonDocument.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        var links = PipeWireAdapter.ParseGraphLinks(graph.RootElement.EnumerateArray());
        var left = links.Where(link => link.To.StartsWith("OpenXLR_ch_left:", StringComparison.Ordinal)).ToArray();
        var right = links.Where(link => link.To.StartsWith("OpenXLR_ch_right:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, left.Length); Assert.Equal(2, right.Length);
        Assert.Single(left.Select(l => l.From).Distinct()); Assert.Single(right.Select(l => l.From).Distinct());
        Assert.NotEqual(left[0].From, right[0].From);
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_missing"));
        MixerConfig restored = MixerConfig.FromSettings(mixer.ExportSettings());
        Assert.Equal(1, restored.Channels.Single(c => c.Id == "right").CaptureMonoChannel);
        Assert.All(mixer.Snapshot().Channels.Where(c => c.CaptureSource is not null), c => Assert.Contains("monitor", c.MutedIn));
    }

    [MonitorPipeWireFact]
    public void MissingOrAmbiguousPrimaryInputsDoNotBorrowAnotherWaveMicrophone()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_multi_wave_parent", "Fixture");
        uint a = pw.CreateVirtualMic("test_Wave_XLR_unitA", "test_multi_wave_parent.monitor", "Unit A");
        pw.CreateVirtualMic("test_Wave_XLR_unitB", "test_multi_wave_parent.monitor", "Unit B");
        Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Count(d => d.Name.StartsWith("test_Wave_XLR_unit", StringComparison.Ordinal)) == 2, TimeSpan.FromSeconds(5)));
        mixer.SetInputDeviceHint("test_Wave_XLR_unit");
        mixer.Build(new MixerConfig { Channels = [new("xlr1", "Microphone") { InputPair = 0 }], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr1")); Assert.NotNull(mixer.InputWarning);
        mixer.SetInputDeviceHint("test_Wave_XLR_unitA");
        Assert.True(SpinWait.SpinUntil(() => IncomingRouteLinks("OpenXLR_ch_xlr1").Length == 2, TimeSpan.FromSeconds(5)));
        Assert.Null(mixer.InputWarning);
        mixer.SetInputDeviceHint(null);
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr1"));
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr1"));
        mixer.SetInputDeviceHint("test_Wave_XLR_unitA");
        Assert.True(SpinWait.SpinUntil(() => IncomingRouteLinks("OpenXLR_ch_xlr1").Length == 2, TimeSpan.FromSeconds(5)));
        pw.UnloadModule(a);
        Assert.True(SpinWait.SpinUntil(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks("OpenXLR_ch_xlr1").Length == 0; }, TimeSpan.FromSeconds(5)));
        Assert.Contains(pw.ListDevices(), d => d.Name == "test_Wave_XLR_unitB");
    }

    [MonitorPipeWireFact]
    public void AnExplicitlyAbsentPrimaryAtStartupDoesNotDiscoverAnAttachedWavePeer()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_absent_primary_parent", "Fixture");
        pw.CreateVirtualMic("test_Wave_XLR_peer", "test_absent_primary_parent.monitor", "Peer");
        Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Any(d => d.Name == "test_Wave_XLR_peer"), TimeSpan.FromSeconds(5)));
        mixer.SetInputDeviceHint(null);
        mixer.Build(new MixerConfig { Channels = [new("xlr1", "Microphone") { InputPair = 0 }], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr1"));
    }

    [MonitorPipeWireFact]
    public void ALongerSerialNeverSubstitutesForTheSelectedMicrophone()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_serial_parent", "Fixture");
        uint selected = pw.CreateVirtualMic("test_Wave_XLR_unitA-00", "test_serial_parent.monitor", "Selected unit");
        pw.CreateVirtualMic("test_Wave_XLR_unitAB-00", "test_serial_parent.monitor", "Peer");
        Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Count(d => d.Name.StartsWith("test_Wave_XLR_unit", StringComparison.Ordinal)) == 2, TimeSpan.FromSeconds(5)));
        var device = new DeviceInfo("Elgato", "Wave XLR", 0x0fd9, 0x007d) { Location = new(1, 2, "1-2", "unitA") };
        mixer.SetInputDeviceHint(device.NodeNameFragment);
        mixer.Build(new MixerConfig { Channels = [new("xlr1", "Microphone") { InputPair = 0 }], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        Assert.True(SpinWait.SpinUntil(() => IncomingRouteLinks("OpenXLR_ch_xlr1").Length == 2, TimeSpan.FromSeconds(5)));
        using (var graph = JsonDocument.Parse(ProcessRunner.Run("pw-dump", []).Stdout))
            Assert.All(PipeWireAdapter.ParseGraphLinks(graph.RootElement.EnumerateArray())
                .Where(link => link.To.StartsWith("OpenXLR_ch_xlr1:", StringComparison.Ordinal)),
                link => Assert.StartsWith("test_Wave_XLR_unitA-00:", link.From));
        pw.UnloadModule(selected);
        Assert.True(SpinWait.SpinUntil(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks("OpenXLR_ch_xlr1").Length == 0; }, TimeSpan.FromSeconds(5)));
        Assert.Contains(pw.ListDevices(), d => d.Name == "test_Wave_XLR_unitAB-00");
    }
}
