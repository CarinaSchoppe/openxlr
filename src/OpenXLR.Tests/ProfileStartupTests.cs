using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ProfileStartupTests
{
    private sealed class Dock : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", "XLR Dock", 0x0fd9, 0x00a6);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, RetainsSettings = false };
        public bool Connected { get; private set; }
        public int Gain = 75;
        public DeviceState ReadState() => new() { GainDb = Gain };
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public void SetGainDb(int db) => Gain = db;
        public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { }
        public void SetVoiceTuneStrength(int value) { } public void SetHpVolumeDb(double db) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { }
        public void SetCompressor(bool on) { }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }

    [MonitorPipeWireFact]
    public async Task StartupProfileWinsOverSavedMixerSettings()
    {
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            new OpenXLR.Core.Mixing.MixerSettings
            {
                UserChannels = [new("game", "Game")], UserMixes = [],
                MixVolumes = new() { ["monitor"] = 0.9 },
            }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            ProfileStore.Save("0fd9:00a6", "Saved", new Profile
            {
                Device = new() { GainDb = 55 },
                Mixer = new() { MixVolumes = new() { ["monitor"] = 0.25 } },
            });
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Saved");
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            Assert.True(mixer.SubmixerEnabled);
            Task recall = hub.RecallOnArrivalAsync("0fd9:00a6");
            Assert.False(recall.IsCompleted);
            try
            {
                await mixer.StartAsync(CancellationToken.None);
                await recall.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("Saved", hub.Snapshot().ActiveProfile);
                Assert.Equal(55, dock.Gain);
                Assert.Equal(0.25, mixer.ExportScene()!.MixVolumes["monitor"]);
            }
            finally { await mixer.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivalWaitsForInitializationAndCanBeCancelled(bool cancel)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            ProfileStore.Save("0fd9:00a6", "Saved", new Profile { Device = new() { GainDb = 55 } });
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Saved");
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            DeviceStateStore.SaveLast("0fd9:00a6", new() { GainDb = 55 });
            Task recall = hub.RecallOnArrivalAsync("0fd9:00a6");
            Assert.False(recall.IsCompleted);
            Assert.Equal(75, dock.Gain);
            if (cancel) lifetime.StopApplication();
            else await mixer.StartAsync(CancellationToken.None);
            await recall.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? 75 : 55, dock.Gain);
            Assert.Equal(cancel ? null : "Saved", hub.Snapshot().ActiveProfile);
            await Task.Delay(1100);
            devices.SweepOnce();
            Assert.Equal(55, DeviceStateStore.LoadLast("0fd9:00a6")!.GainDb);
            if (!cancel)
            {
                using var gain = JsonDocument.Parse("75");
                using var locked = JsonDocument.Parse("true");
                Assert.Null(devices.Apply("gain", gain.RootElement));
                Assert.Null(devices.Apply("gainLock", locked.RootElement));
                var loaded = await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Saved"}""");
                Assert.True(loaded.Ok);
                Assert.Equal(55, dock.Gain);
                Assert.True(devices.Snapshot().State!.GainLocked);
                Assert.Equal("gain is locked", devices.Apply("gain", gain.RootElement));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }
}
