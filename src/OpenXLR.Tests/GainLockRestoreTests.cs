using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// The manager keeps gain locks and last settings under the configuration
// directory, which these tests redirect.
[Collection("xdg-config")]
public sealed class GainLockRestoreTests
{
    /// <summary>
    /// A dock that forgets its settings when it loses power: it comes back at
    /// the gain its firmware chooses, whatever it was set to before.
    /// </summary>
    private sealed class ForgetfulDock : IAudioDevice
    {
        public int FirmwareGainDb = 43;
        public int FirmwareGain2Db = 43;
        public readonly List<int> GainWrites = [];
        public readonly List<int> Gain2Writes = [];
        public void Dispose() { }
        public DeviceInfo Info { get; init; } = new("Elgato", "XLR Dock", 0x0fd9, 0x00a6);
        public DeviceCapabilities Capabilities { get; init; } = new() { Gain = true, Mute = true, Phantom = true, RetainsSettings = false };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public DeviceState ReadState() => new() { GainDb = FirmwareGainDb, Gain2Db = FirmwareGain2Db };
        public void SetGainDb(int db) { GainWrites.Add(db); FirmwareGainDb = db; }
        public void SetGain2Db(int db) { Gain2Writes.Add(db); FirmwareGain2Db = db; }
        public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AProfileLoadedByHandRestoresBothLockedGains(int inputs)
    {
        await WithConfigDirAsync(async () =>
        {
            new DaemonSettings { Submixer = false }.Save();
            var dock = new ForgetfulDock
            {
                Capabilities = new() { Gain = true, XlrInputs = inputs, RetainsSettings = false },
            };
            using DeviceManager manager = Connected(dock);
            using var locked = JsonDocument.Parse("true");
            Assert.Null(manager.Apply("gainLock", locked.RootElement));
            ProfileStore.Save("0fd9:00a6", "Saved", new()
            {
                Device = new() { GainDb = 20, Gain2Db = 30 },
            });
            using var mixer = new MixerService(NullLogger<MixerService>.Instance,
                new ConfigurationBuilder().Build(), manager);
            var hub = new WebSocketHub(manager, mixer, NullLogger<WebSocketHub>.Instance, new Lifetime());

            var result = await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Saved"}""");

            Assert.True(result.Ok);
            Assert.Equal([20], dock.GainWrites);
            Assert.Equal(inputs == 2 ? [30] : Array.Empty<int>(), dock.Gain2Writes);
            Assert.True(manager.Snapshot().State!.GainLocked);
            using var gain = JsonDocument.Parse("10");
            Assert.Equal("gain is locked", manager.Apply("gain", gain.RootElement));
            if (inputs == 2) Assert.Equal("gain is locked", manager.Apply("gain2", gain.RootElement));
            Assert.Equal([20], dock.GainWrites);
            Assert.Equal(inputs == 2 ? [30] : Array.Empty<int>(), dock.Gain2Writes);
        });
    }

    private static async Task WithConfigDirAsync(Func<Task> body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-test-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { await body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void WithConfigDir(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static DeviceManager Connected(ForgetfulDock dock)
    {
        var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
            new ConfigurationBuilder().Build(), () => [dock]);
        manager.SweepOnce();
        return manager;
    }

    [Fact]
    public void TheLockedGainIsGivenBackToADockThatForgotIt()
    {
        WithConfigDir(_ =>
        {
            // What the user set and locked, saved as the last settings seen.
            DeviceStateStore.SaveLast("0fd9:00a6", new DeviceState { GainDb = 55 });
            var dock = new ForgetfulDock();          // powers up at 43 dB
            DeviceManager manager = Connected(dock);
            Assert.Null(manager.Apply("gainLock", JsonDocument.Parse("true").RootElement));

            string? status = manager.RestoreLastState();

            Assert.NotNull(status);
            Assert.Equal([55], dock.GainWrites);      // the lock did not eat the restore
            Assert.Equal(55, manager.Snapshot().State!.GainDb);
        });
    }

    [Fact]
    public void TheLockStillRefusesAGainChangeFromAClient()
    {
        WithConfigDir(_ =>
        {
            var dock = new ForgetfulDock { FirmwareGainDb = 55 };
            DeviceManager manager = Connected(dock);
            Assert.Null(manager.Apply("gainLock", JsonDocument.Parse("true").RootElement));

            string? error = manager.Apply("gain", JsonDocument.Parse("20").RootElement);

            Assert.Equal("gain is locked", error);
            Assert.Empty(dock.GainWrites);
            Assert.Equal(55, manager.Snapshot().State!.GainDb);
        });
    }

    [Fact]
    public void AnUnmarkedDeviceRestoreStillLeavesALockedGainAlone()
    {
        WithConfigDir(_ =>
        {
            var dock = new ForgetfulDock { FirmwareGainDb = 55 };
            DeviceManager manager = Connected(dock);
            Assert.Null(manager.Apply("gainLock", JsonDocument.Parse("true").RootElement));

            Assert.Null(manager.ApplyProfile(new DeviceState { GainDb = 20 }));

            Assert.Empty(dock.GainWrites);
            Assert.Equal(55, manager.Snapshot().State!.GainDb);
        });
    }

    [Fact]
    public void TheLockStillRefusesResettingTheProToItsBaseline()
    {
        WithConfigDir(_ =>
        {
            var dock = new ForgetfulDock
            {
                Info = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4),
                Capabilities = new() { Gain = true, XlrInputs = 2, RetainsSettings = true },
            };
            using DeviceManager manager = Connected(dock);
            using var locked = JsonDocument.Parse("true");
            Assert.Null(manager.Apply("gainLock", locked.RootElement));

            Assert.Equal("resetDevice: release the gain lock first", manager.ResetToDefaults());

            Assert.Empty(dock.GainWrites);
            Assert.Empty(dock.Gain2Writes);
        });
    }

    [Fact]
    public void WithoutTheLockNothingAboutRestoringChanges()
    {
        WithConfigDir(_ =>
        {
            DeviceStateStore.SaveLast("0fd9:00a6", new DeviceState { GainDb = 55 });
            var dock = new ForgetfulDock();
            DeviceManager manager = Connected(dock);

            manager.RestoreLastState();

            Assert.Equal([55], dock.GainWrites);
        });
    }
}
