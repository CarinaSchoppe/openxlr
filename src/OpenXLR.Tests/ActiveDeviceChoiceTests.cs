using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The interface setActiveDevice chose is driven again after a restart: the
/// desk's Wave XLR Pro and Wave:3 MK.2, with OPENXLR_DEVICE naming the Pro,
/// came back on the Pro after every restart. The saved choice wins over the
/// environment, is waited for when it enumerates late, and gives way to a
/// stand-in only when it stays away.
/// </summary>
[Collection("xdg-config")]
public sealed class ActiveDeviceChoiceTests
{
    private sealed class Unit(string model, ushort productId, string serial, string port = "1-2") : IAudioDevice
    {
        public int Connects;
        public DeviceInfo Info { get; set; } = new("Elgato", model, 0x0fd9, productId) { Location = new(1, 2, port, serial) };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true };
        public bool Connected { get; private set; }
        public void Connect() { Connects++; Connected = true; }
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public DeviceState ReadState() => new() { GainDb = 30 };
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static Unit Pro() => new("Wave XLR Pro", 0x00b4, "AAY3H5481241GS");
    private static Unit Mk2() => new("Wave:3 MK.2", 0x00ca, "AD0JH55014KSN3", "1-4");

    /// <summary>The primary's manager as the daemon makes it, with OPENXLR_DEVICE naming the Pro.</summary>
    private static DeviceManager Primary(Func<IReadOnlyList<IAudioDevice>> detect, ManualTime? time = null)
        => new(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), detect,
            time: time ?? new ManualTime(), preferredModel: 0x00b4, keepsChoice: true);

    [Fact]
    public void TheChosenUnitIsDrivenAgainAfterARestart()
    {
        WithConfigDir(() =>
        {
            Unit pro = Pro(), mk2 = Mk2();
            using (DeviceManager first = Primary(() => [pro, mk2]))
            {
                first.SweepOnce();
                Assert.Equal(pro.Info.InstanceId, first.ActiveInfo?.InstanceId);
                Assert.Null(first.SetActiveDevice(mk2.Info.InstanceId));
                first.SweepOnce();
                Assert.Equal(mk2.Info.InstanceId, first.ActiveInfo?.InstanceId);
                first.SuspendSession();
            }
            Assert.Contains(mk2.Info.InstanceId, File.ReadAllText(ActiveDeviceChoice.PathName), StringComparison.Ordinal);

            // A fresh start, both units on the bus in either order: the MK.2,
            // and the Pro is never opened on the way.
            foreach (IReadOnlyList<IAudioDevice> order in new IReadOnlyList<IAudioDevice>[] { [pro, mk2], [mk2, pro] })
            {
                pro.Connects = 0;
                using DeviceManager restarted = Primary(() => order);
                restarted.SweepOnce();
                Assert.Equal(mk2.Info.InstanceId, restarted.ActiveInfo?.InstanceId);
                Assert.Equal(0, pro.Connects);
                restarted.SuspendSession();
            }
        });
    }

    [Fact]
    public void AChosenUnitThatEnumeratesLateIsWaitedFor()
    {
        WithConfigDir(() =>
        {
            Unit pro = Pro(), mk2 = Mk2();
            Assert.Null(ActiveDeviceChoice.Save(mk2.Info.InstanceId));
            var time = new ManualTime();
            IReadOnlyList<IAudioDevice> attached = [pro];
            using DeviceManager manager = Primary(() => attached, time);
            for (int i = 0; i < 9; i++, time.Advance(TimeSpan.FromSeconds(1))) manager.SweepOnce();
            Assert.Null(manager.ActiveInfo);
            Assert.Equal(0, pro.Connects);

            attached = [pro, mk2];
            manager.SweepOnce();
            Assert.Equal(mk2.Info.InstanceId, manager.ActiveInfo?.InstanceId);
            Assert.Equal(0, pro.Connects);
            manager.SuspendSession();
        });
    }

    [Fact]
    public void AnAbsentChoiceGetsAStandInAfterTheWaitThatGivesWayWhenItComes()
    {
        WithConfigDir(() =>
        {
            Unit pro = Pro(), mk2 = Mk2(), xlr = new("Wave XLR", 0x007d, "XLRSERIAL", "1-6");
            Assert.Null(ActiveDeviceChoice.Save(mk2.Info.InstanceId));
            var time = new ManualTime();
            // The Wave XLR is first on the bus; OPENXLR_DEVICE names the Pro.
            IReadOnlyList<IAudioDevice> attached = [xlr, pro];
            using DeviceManager manager = Primary(() => attached, time);
            manager.SweepOnce();
            Assert.Null(manager.ActiveInfo);
            time.Advance(DeviceManager.RestoredChoiceWait);
            manager.SweepOnce();
            Assert.Equal(pro.Info.InstanceId, manager.ActiveInfo?.InstanceId);

            attached = [xlr, pro, mk2];
            manager.SweepOnce();
            Assert.Equal(mk2.Info.InstanceId, manager.ActiveInfo?.InstanceId);
            Assert.False(pro.Connected);

            // Away again after it was driven: the rule of a choice made in
            // this run, which keeps the other units free while two remain.
            attached = [xlr, pro];
            mk2.Disconnect();
            for (int i = 0; i < 3; i++) manager.SweepOnce();
            Assert.Null(manager.ActiveInfo);
            manager.SuspendSession();
        });
    }

    [Fact]
    public void AChosenUnitWithoutASerialIsFoundOnAnotherPort()
    {
        WithConfigDir(() =>
        {
            var moved = new Unit("Wave:3 MK.2", 0x00ca, serial: "", port: "1-4");
            var before = new Unit("Wave:3 MK.2", 0x00ca, serial: "", port: "1-3");
            Assert.NotEqual(before.Info.InstanceId, moved.Info.InstanceId);
            Assert.Null(ActiveDeviceChoice.Save(before.Info.InstanceId));
            Unit pro = Pro();
            using DeviceManager manager = Primary(() => [pro, moved]);
            manager.SweepOnce();
            Assert.Equal(moved.Info.InstanceId, manager.ActiveInfo?.InstanceId);
            manager.SuspendSession();
        });
    }

    [Theory]
    [InlineData("{\"device\":\"../escape\"}")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void AnUnreadableChoiceFallsBackToTheEnvironment(string text)
    {
        WithConfigDir(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveDeviceChoice.PathName)!);
            File.WriteAllText(ActiveDeviceChoice.PathName, text);
            Unit pro = Pro(), mk2 = Mk2();
            using DeviceManager manager = Primary(() => [mk2, pro]);
            manager.SweepOnce();
            Assert.Equal(pro.Info.InstanceId, manager.ActiveInfo?.InstanceId);
            manager.SuspendSession();
        });
    }

    [Fact]
    public void OnlyThePrimarysManagerKeepsTheChoice()
    {
        WithConfigDir(() =>
        {
            Unit pro = Pro(), mk2 = Mk2();
            Assert.Null(ActiveDeviceChoice.Save(mk2.Info.InstanceId));
            // An additional interface's manager starts from no saved choice
            // and writes none.
            using var additional = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(),
                () => [pro], time: new ManualTime());
            additional.SweepOnce();
            Assert.Equal(pro.Info.InstanceId, additional.ActiveInfo?.InstanceId);
            Assert.Null(additional.SetActiveDevice(pro.Info.InstanceId));
            Assert.Contains(mk2.Info.InstanceId, File.ReadAllText(ActiveDeviceChoice.PathName), StringComparison.Ordinal);
            additional.SuspendSession();
        });
    }

    [Theory]
    [InlineData("0fd9:00ca@8bd2645799bcc225", true)]
    [InlineData("0fd9:00ca", true)]
    [InlineData("0FD9:00CA", false)]
    [InlineData("0fd9:00ca@xyz", false)]
    [InlineData("../escape", false)]
    [InlineData(null, false)]
    public void TheSavedChoiceIsAnInstanceOrModelId(string? device, bool valid)
    {
        Assert.Equal(valid, ActiveDeviceChoice.Valid(device));
        if (valid) Assert.Equal(0x00ca, ActiveDeviceChoice.ProductId(device!));
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-choice-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
