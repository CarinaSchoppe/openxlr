using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WaveInterfaceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-wave-units-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    public WaveInterfaceTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
    private static UsbLocation Location(string serial, byte address = 2) => new(1, address, "1-2", serial);
    private static IConfiguration Configuration => new ConfigurationBuilder().Build();
    private static DeviceManager Manager(Func<IReadOnlyList<IAudioDevice>> detect) => new(NullLogger<DeviceManager>.Instance, Configuration, detect);

    [Fact]
    public void PersistentIdentitySurvivesAddressAndPortChangesButSeparatesSameModelUnits()
    {
        var a = new DeviceInfo("Elgato", "Wave XLR", 0x0fd9, 0x007d) { Location = Location("unitA") };
        var replugged = a with { Location = new(2, 9, "2-4", "unitA") };
        var b = a with { Location = Location("unitB") };
        Assert.Equal(a.InstanceId, replugged.InstanceId);
        Assert.NotEqual(a.InstanceId, b.InstanceId);
        Assert.True(UsbLocation.IsInstanceId(a.InstanceId));
        Assert.EndsWith("_unitA-", a.NodeNameFragment);
        Assert.Contains(a.NodeNameFragment, "alsa_input.usb-Elgato_Wave_XLR_unitA-00.analog-stereo");
        Assert.DoesNotContain(a.NodeNameFragment, "alsa_input.usb-Elgato_Wave_XLR_unitAB-00.analog-stereo");
        Assert.DoesNotContain(a.NodeNameFragment, "alsa_input.usb-Elgato_Wave_XLR_unitA_second-00.analog-stereo");
        Assert.NotEqual((Location("", 2) with { Port = "1-2" }).Key, (Location("", 2) with { Port = "1-3" }).Key);
        var policy = new HungTransferPolicy();
        for (int i = 0; i < HungTransferPolicy.Limit; i++) policy.NoteHung(a.InstanceId);
        Assert.True(policy.IsSetAside(a.InstanceId)); Assert.False(policy.IsSetAside(b.InstanceId));
        policy.Returned(a.InstanceId); Assert.Equal(0, policy.HungCount(a.InstanceId));
    }

    [Fact]
    public void AChangedUsbAddressRecoversASetAsideUnitEvenWhenTheUnplugTickWasMissed()
    {
        var previous = DeviceManager.HungReconnectDelay;
        DeviceManager.HungReconnectDelay = TimeSpan.Zero;
        try
        {
            using var device = new FakeDevice("unitA") { Hanging = true };
            using var manager = Manager(() => [device]);
            for (int i = 0; i < HungTransferPolicy.Limit; i++) manager.SweepOnce();
            Assert.NotNull(manager.Warning); Assert.Null(manager.ActiveInfo);
            device.Hanging = false; device.MoveToAddress(7); manager.SweepOnce();
            Assert.Null(manager.Warning); Assert.Equal(device.Info.InstanceId, manager.ActiveInfo?.InstanceId);
            manager.SuspendSession();
        }
        finally { DeviceManager.HungReconnectDelay = previous; }
    }

    [Fact]
    public void ScopedDeviceConstructionNeverLeaksTheSelectionToTheNextBackend()
    {
        using var selected = UsbTransport.At(Location("unitA"), () => new WaveXlrMk1Device());
        using var other = new WaveXlrMk1Device();
        Assert.NotNull(selected.Info.Location); Assert.Null(other.Info.Location);
        Assert.Throws<IOException>(() => UsbTransport.At<int>(Location("unitB"), () => throw new IOException("failed")));
        using var afterFailure = new WaveXlrMk1Device(); Assert.Null(afterFailure.Info.Location);
    }

    [Fact]
    public void ExactOpenFramesNeverFallBackToTheFirstMatchingProduct()
    {
        using var backend = new FakeUsb();
        using var requests = new MemoryStream(); using var replies = new MemoryStream();
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Open(0x0fd9, 0x007d, Location("unitA", 8)));
        requests.Position = 0; UsbHelperProtocol.Serve(requests, replies, backend);
        Assert.Equal((byte)1, backend.Bus); Assert.Equal((byte)8, backend.Address); Assert.Equal(0, backend.LegacyOpens);
        replies.Position = 0; Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(UsbHelperProtocol.ReadFrame(replies)!));
        using var unsupported = new LegacyUsb(); using var refused = new MemoryStream();
        requests.Position = 0; UsbHelperProtocol.Serve(requests, refused, unsupported);
        Assert.Equal(0, unsupported.LegacyOpens);
        refused.Position = 0; Assert.Equal(UsbHelperProtocol.NotOpened, BinaryPrimitives.ReadInt32LittleEndian(UsbHelperProtocol.ReadFrame(refused)!));
    }

    [Fact]
    public void PrimarySelectionSwitchesBetweenIdenticalModelsAndRejectsForeignVendors()
    {
        using var a = new FakeDevice("unitA"); using var b = new FakeDevice("unitB");
        using var manager = Manager(() => [a, b]);
        manager.SweepOnce(); Assert.True(a.Connected); Assert.False(b.Connected);
        Assert.Equal(2, manager.Detected().Count);
        Assert.NotNull(manager.SetActiveDevice("aaaa:007d"));
        Assert.Null(manager.SetActiveDevice(b.Info.InstanceId)); manager.SweepOnce();
        Assert.False(a.Connected); Assert.True(b.Connected); Assert.Equal(b.Info.InstanceId, manager.ActiveInfo?.InstanceId);
        manager.SuspendSession(); manager.SweepOnce(); Assert.False(b.Connected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingThePrimaryReleasesTheOldTransportEvenWhenDisconnectFails(bool disconnectFails)
    {
        using var a = new FakeDevice("unitA") { DisconnectFails = disconnectFails };
        using var b = new FakeDevice("unitB"); using var manager = Manager(() => [a, b]);
        manager.SweepOnce(); Assert.True(a.Connected);
        Assert.Null(manager.SetActiveDevice(b.Info.InstanceId));
        Assert.Equal(1, a.DisposeCount);
        manager.SweepOnce(); Assert.True(b.Connected);
        manager.SuspendSession(); Assert.Equal(1, b.DisposeCount);
    }

    [Fact]
    public void SwitchingThePrimaryFlushesSettingsThatAreStillWaitingForTheDebounce()
    {
        using var a = new FakeDevice("unitA", retainsSettings: false);
        using var b = new FakeDevice("unitB"); using var manager = Manager(() => [a, b]);
        manager.SweepOnce(); manager.MarkRestored();
        Assert.Null(manager.Apply("gain", JsonSerializer.SerializeToElement(44)));
        Assert.Null(DeviceStateStore.LoadLast("0fd9:007d"));
        Assert.Null(manager.SetActiveDevice(b.Info.InstanceId));
        Assert.Equal(44, DeviceStateStore.LoadLast("0fd9:007d")?.GainDb);
        manager.SweepOnce(); Assert.True(b.Connected); manager.SuspendSession();
    }

    [Fact]
    public async Task AdditionalInterfacesRemainIsolatedDuringControlsProfileRecallAndPrimaryHandoff()
    {
        using var a = new FakeDevice("unitA"); using var b = new FakeDevice("unitB");
        using var primary = Manager(() => [a, b]); primary.SweepOnce();
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => [a.Fork(), b.Fork()]);
        await interfaces.StartAsync(CancellationToken.None);
        try
        {
            Wait(() => interfaces.Snapshot().Count == 2);
            Assert.Null(interfaces.SetEnabled(b.Info.InstanceId, true));
            Wait(() => interfaces.Snapshot().Any(s => s.Id == b.Info.InstanceId && s.Connected));
            Assert.Null(interfaces.Apply(b.Info.InstanceId, "gain", JsonSerializer.SerializeToElement(52)));
            Assert.Equal(52, b.Gain); Assert.Equal(30, a.Gain);
            var profile = interfaces.CaptureProfile(); Assert.Equal(52, profile[b.Info.InstanceId].GainDb);
            b.Gain = 20; Assert.Null(interfaces.ApplyProfile(profile)); Assert.Equal(52, b.Gain);
            ProfileStore.Save("0fd9:007d", "Rig", new() { AdditionalDevices = profile });
            Assert.Equal(52, ProfileStore.Load("0fd9:007d", "Rig")!.AdditionalDevices![b.Info.InstanceId].GainDb);
            Assert.Null(primary.SetActiveDevice(b.Info.InstanceId)); primary.SweepOnce();
            Wait(() => interfaces.Snapshot().Any(s => s.Id == b.Info.InstanceId && s.Active));
            Assert.Null(interfaces.Apply(b.Info.InstanceId, "gain", JsonSerializer.SerializeToElement(43)));
            Assert.Equal(43, b.Gain); Assert.False(a.Connected);
            Assert.Null(interfaces.SetEnabled(b.Info.InstanceId, false));
            Assert.True(b.Connected); // disabling its additional role cannot close the primary
        }
        finally { await interfaces.StopAsync(CancellationToken.None); primary.SuspendSession(); }
        static void Wait(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProfileRecallReadsAHardwareDialChangeBetweenPollTicks()
    {
        using var device = new FakeDevice("unitA"); using var manager = Manager(() => [device]);
        manager.SweepOnce(); Assert.Equal(30, manager.Snapshot().State!.GainDb);
        device.Gain = 12; // hardware changed after the last poll, before recall
        Assert.Null(manager.ApplyProfile(new() { GainDb = 30 }, restoring: true));
        Assert.Equal(30, device.Gain); Assert.Equal(30, manager.Snapshot().State!.GainDb);
        manager.SuspendSession();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("0fd9:007d")]
    [InlineData("0fd9:007d@xxxxxxxxxxxxxxxx")]
    public void InvalidPhysicalIdsAndCorruptPreferencesAreNeverReplaced(string id)
    {
        using var primary = Manager(() => []);
        string path = OpenXLR.Core.OpenXlrPaths.ConfigFile("wave-interfaces.json");
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, "[\"bad\"]");
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => []);
        Assert.NotNull(interfaces.SetEnabled(id, false));
        Assert.NotNull(interfaces.SetEnabled("0fd9:007d@0123456789abcdef", false));
        Assert.Equal("[\"bad\"]", File.ReadAllText(path));
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "setWaveInterfaceEnabled", Device = id, Value = JsonSerializer.SerializeToElement(true) }, mixer, _ => null));
        ProfileStore.Save("0fd9:007d", "Invalid", new() { AdditionalDevices = new() { [id] = new() } });
        Assert.Throws<JsonException>(() => ProfileStore.Load("0fd9:007d", "Invalid"));
    }

    [Fact]
    public void RememberedOfflineInterfacesRemainVisibleAndCanBeForgotten()
    {
        const string id = "0fd9:007d@0123456789abcdef";
        using var primary = Manager(() => []);
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(OpenXLR.Core.OpenXlrPaths.ConfigFile("wave-interfaces.json"), JsonSerializer.Serialize(new[] { id }));
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => []);
        var offline = Assert.Single(interfaces.Snapshot());
        Assert.Equal(id, offline.Id); Assert.True(offline.Enabled); Assert.False(offline.Connected);
        Assert.Null(interfaces.SetEnabled(id, false)); Assert.Empty(interfaces.Snapshot());
    }

    [Fact]
    public void ExcessiveEnabledPreferencesArePreservedAndReported()
    {
        using var primary = Manager(() => []);
        string path = OpenXLR.Core.OpenXlrPaths.ConfigFile("wave-interfaces.json");
        string text = JsonSerializer.Serialize(Enumerable.Range(0, 5).Select(i => $"0fd9:007d@{i:x16}").ToArray());
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, text);
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => []);
        Assert.NotNull(interfaces.Warning); Assert.NotNull(interfaces.SetEnabled("0fd9:007d@0000000000000000", false));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public async Task RetiredControlsCannotSendQueuedGainOrModifyANewConnection()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        var model = new WaveInterfaceViewModel(client, main, "0fd9:007d@0123456789abcdef");
        model.Apply(JsonNode.Parse("""{"connected":true,"capabilities":{"gain":true},"state":{"gainDb":30}}""")!);
        model.Gain = 50; model.Retire(); SliderSync.FlushPending();
        Assert.False(model.CanControl); Assert.False(model.CanEnable);
        model.Gain = 60; Assert.Equal(50, model.Gain);
        await model.AddInputAsync(); Assert.Null(model.Error);
    }

    [Fact]
    public async Task ADelayedEnableErrorCannotChangeARetiredInterface()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() != "setWaveInterfaceEnabled") continue;
                received.TrySetResult(); await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error = "stale error" }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start(); await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var main = new MainViewModel(client);
        var model = new WaveInterfaceViewModel(client, main, "0fd9:007d@0123456789abcdef");
        var enabling = model.EnableAsync(true);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Retire(); release.TrySetResult(); await enabling;
        Assert.False(model.Enabled); Assert.Null(model.Error); Assert.False(model.CanEnable);
    }

    private class LegacyUsb : IUsbTransport
    {
        public int LegacyOpens;
        public bool IsOpen => false;
        public bool Open(ushort vid, ushort pid) { LegacyOpens++; return true; }
        public void Close() { }
        public int ControlTransfer(byte t, byte r, ushort v, ushort i, byte[] data, ushort len, uint timeout) => 0;
        public void Dispose() { }
    }
    private sealed class FakeUsb : LegacyUsb, IUsbTransport
    {
        public byte Bus, Address;
        public bool Open(ushort vid, ushort pid, byte bus, byte address) { Bus = bus; Address = address; return true; }
    }
    private sealed class FakeDevice(string serial, FakeDevice.Hardware? shared = null, bool retainsSettings = true) : IAudioDevice
    {
        public DeviceInfo Info { get; private set; } = new("Elgato", "Wave XLR", 0x0fd9, 0x007d) { Location = Location(serial) };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, RetainsSettings = retainsSettings };
        public bool Connected { get; private set; }
        internal sealed class Hardware { public int Gain = 30; }
        private readonly Hardware _hardware = shared ?? new();
        public FakeDevice Fork() => new(serial, _hardware, Capabilities.RetainsSettings);
        public int Gain { get => _hardware.Gain; set => _hardware.Gain = value; }
        public void Connect() => Connected = true;
        public bool DisconnectFails;
        public int DisposeCount;
        public void Disconnect() { Connected = false; if (DisconnectFails) throw new IOException("disconnect failed"); }
        public void Dispose() { DisposeCount++; Connected = false; }
        public bool Hanging;
        public void MoveToAddress(byte address) => Info = Info with { Location = Location(serial, address) };
        public DeviceState ReadState() => Hanging ? throw new UsbHungException("hung") : new() { GainDb = Gain };
        public void SetGainDb(int db) => Gain = db;
        public void SetMute(bool on) { } public void SetLowCut(bool on) { } public void SetExpander(bool on) { }
        public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int value) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }
}
