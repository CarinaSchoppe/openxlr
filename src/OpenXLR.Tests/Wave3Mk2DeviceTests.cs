using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave:3 MK.2 on the Wave XLR MK.2's backend, against a unit modelled as
/// the blocks the maintainer's unit returned: bank 0x0103 only, the four
/// controls verified on it and nothing else advertised or written, the flag
/// bits nobody has identified on this model kept out of the state and the
/// block, and the EQ gated on this model's own firmware numbering.
/// </summary>
public sealed class Wave3Mk2DeviceTests
{
    /// <summary>Block 0x0004 as the unit read before anything was written: gain 34 dB, flags 0x62, byte 10 0x10, EQ switch off.</summary>
    private const string SettingsBlock = "226200d4200a00c8001410f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8";

    private sealed class Unit : IUsbTransport
    {
        public bool IsOpen { get; private set; }
        public readonly Dictionary<ushort, byte[]> Blocks = new()
        {
            [0x0000] = Identity(0, 2, 3),
            [0x0001] = Convert.FromHexString("320102030200"),
            [0x0004] = Convert.FromHexString(SettingsBlock),
            [0x0005] = Convert.FromHexString("3000"),
        };
        public readonly List<(byte Type, ushort Block, ushort Bank, byte[] Data)> Calls = [];

        /// <summary>The unit's identity block: <c>01 05 02 02 00</c>, then the version at 5 to 7.</summary>
        public static byte[] Identity(byte major, byte minor, byte patch)
        {
            byte[] block = new byte[117];
            block[0] = 0x01; block[1] = 0x05; block[2] = 0x02; block[3] = 0x02;
            block[5] = major; block[6] = minor; block[7] = patch;
            return block;
        }

        public bool Open(ushort vendor, ushort product)
        {
            Assert.Equal(0x0fd9, vendor);
            Assert.Equal(0x00ca, product);
            return IsOpen = true;
        }

        public void Close() => IsOpen = false;
        public void Dispose() => Close();

        public int ControlTransfer(byte type, byte request, ushort block, ushort bank, byte[] data, ushort length, uint timeout)
        {
            Assert.True(IsOpen);
            Assert.Equal(1, request);
            Calls.Add((type, block, bank, data.Take(length).ToArray()));
            // The unit answers at 0x0103 (and 0x0003); 0x0203 and above stall.
            if (bank != 0x0103 || !Blocks.TryGetValue(block, out byte[]? held)) return -9;
            if (type == 0xC1)
            {
                // Every read answers in full, zero padded, whatever was asked for.
                Array.Clear(data);
                Array.Copy(held, data, Math.Min(length, held.Length));
                return length;
            }
            Blocks[block] = data.Take(length).ToArray();
            return length;
        }

        public IEnumerable<(ushort Block, byte[] Data)> Writes => Calls.Where(c => c.Type == 0x41).Select(c => (c.Block, c.Data));
    }

    private static Wave3Mk2Device Connected(Unit unit)
    {
        var device = new Wave3Mk2Device(unit);
        device.Connect();
        return device;
    }

    [Fact]
    public void IsAUsbMicrophoneWithTheFourVerifiedControls()
    {
        var unit = new Unit();
        using Wave3Mk2Device device = Connected(unit);
        Assert.Equal("Wave:3 MK.2", device.Info.Model);
        Assert.Equal("0fd9:00ca", device.Info.InstanceId);
        // The node-name fragment udev builds from "Elgato Wave:3 MK.2".
        Assert.Equal("Wave_3_MK.2", device.Info.ModelNameFragment);
        DeviceCapabilities c = device.Capabilities;
        Assert.True(c.Gain && c.Mute && c.HpVolume && c.Crossfade);
        Assert.False(c.Phantom || c.LowCut || c.Expander || c.VoiceTune || c.Compressor || c.ClipGuard || c.LowImpedance);
        Assert.False(c.XlrJacks);
        Assert.Equal(1, c.XlrInputs);
        Assert.Equal(1, c.HpOutputs);
        Assert.Equal(40, c.GainMaxDb);
        Assert.True(c.PhysicalControls);
        Assert.True(c.RetainsSettings);
        Assert.False(c.Ducking || c.OutputRouting || c.AuxInput || c.LineInput);
        Assert.Null(device.ConnectionNote);
        // One bank, never probed against another: every transfer at 0x0103.
        Assert.All(unit.Calls, call => Assert.Equal(0x0103, call.Bank));
        Assert.All(unit.Calls, call => Assert.Equal(0xC1, call.Type));
    }

    [Fact]
    public void ReadsTheVerifiedFieldsAndLeavesTheUnknownBitsOut()
    {
        using Wave3Mk2Device device = Connected(new Unit());
        DeviceState s = device.ReadState();
        Assert.Equal(34, s.GainDb);
        Assert.False(s.Mute);
        Assert.Equal(-12, s.HpVolumeDb);
        Assert.Equal(50, s.Crossfade);
        // Flags 0x62 hold bits 0x02, 0x20 and 0x40, phantom, expander and
        // voice tune on the XLR MK.2; on this microphone nobody knows them.
        Assert.False(s.Phantom);
        Assert.False(s.Expander);
        Assert.False(s.VoiceTune);
        Assert.Equal(0, s.VoiceTuneStrength);
        Assert.False(s.ClipGuard);
        Assert.False(s.LowImpedance);
    }

    [Fact]
    public void EachWriteSendsTheWholeBlockWithOneFieldChanged()
    {
        var unit = new Unit();
        using Wave3Mk2Device device = Connected(unit);
        byte[] settings = Convert.FromHexString(SettingsBlock);

        device.SetGainDb(30);
        (ushort block, byte[] data) = unit.Writes.Last();
        Assert.Equal(0x0004, block);
        Assert.Equal(38, data.Length);
        Assert.Equal(30, data[0]);
        Assert.Equal(settings[1..], data[1..]);

        // The kernel's capture control stops at 40 dB, and so does the gain.
        device.SetGainDb(45);
        Assert.Equal(40, unit.Blocks[0x0004][0]);

        device.SetMute(true);
        Assert.Equal(0x63, unit.Blocks[0x0004][1]);
        device.SetMute(false);
        Assert.Equal(0x62, unit.Blocks[0x0004][1]);

        device.SetHpVolumeDb(-20);
        Assert.Equal(new byte[] { 0x50, 0x00 }, unit.Writes.Last().Data);
        device.SetCrossfade(100);
        Assert.Equal(Convert.FromHexString("640102030200"), unit.Writes.Last().Data);

        DeviceState s = device.ReadState();
        Assert.Equal((40, false, -20.0, 100), (s.GainDb, s.Mute, s.HpVolumeDb, s.Crossfade));
        Assert.All(unit.Calls, call => Assert.Equal(0x0103, call.Bank));
        // Everything but the gain and the mute bit is as the unit had it.
        Assert.Equal(settings[1..], unit.Blocks[0x0004][1..]);
    }

    [Fact]
    public void AControlTheModelLacksIsRefusedBeforeAnyTransfer()
    {
        var unit = new Unit();
        using Wave3Mk2Device device = Connected(unit);
        unit.Calls.Clear();
        Action[] lacking =
        [
            () => device.SetPhantom(true), () => device.SetLowCut(true), () => device.SetExpander(true),
            () => device.SetVoiceTune(true), () => device.SetVoiceTuneStrength(50), () => device.SetCompressor(true),
            () => device.SetClipGuard(false), () => device.SetLowImpedance(true),
        ];
        foreach (Action set in lacking)
            Assert.Contains("Wave:3 MK.2 has no", Assert.Throws<InvalidOperationException>(set).Message);
        Assert.Empty(unit.Calls);
        // The daemon refuses the same controls by name before reaching the device.
        foreach (string control in new[] { "phantom", "lowCut", "expander", "voiceTune", "voiceTuneStrength", "compressor", "clipGuard", "lowImpedance", "gain2" })
            Assert.NotNull(DeviceManager.UnsupportedControlReason(device.Capabilities, control));
        foreach (string control in new[] { "gain", "mute", "hpVolumeDb", "crossfade" })
            Assert.Null(DeviceManager.UnsupportedControlReason(device.Capabilities, control));
    }

    [Fact]
    public void TheEqRunsOnTheUnitFromThisModelsOwnFirmware()
    {
        var unit = new Unit();
        using Wave3Mk2Device device = Connected(unit);
        // 0.2.3 in this model's numbering, not the XLR MK.2's 1.0.15.
        Assert.Equal("0.2.3", device.Firmware);
        Assert.True(device.Capabilities.HardwareEq);
        Assert.Equal(new DeviceEqSupport(true) { MinimumFirmware = "0.2.3" }, device.EqSupport);
        device.ReadState();
        DeviceEq held = device.ReadEq(1)!;
        Assert.False(held.Enabled);
        // The bell the unit was measured with: +12 dB at 1 kHz, Q 1, the other
        // bands off at their held values.
        DeviceEqBand[] bands = [.. held.Bands!.Select(b => b with { Enabled = false })];
        bands[0] = new DeviceEqBand(true, 2, 48, 1000, 0);
        unit.Calls.Clear();
        Assert.True(device.SetEq(1, new DeviceEq(true, bands)));
        (ushort block, byte[] data) = Assert.Single(unit.Writes);
        Assert.Equal(0x0004, block);
        Assert.Equal("226201d4200a00c8001410f4040a00640006" + "0530e80300" + "020de30000" + "04f45c03f2" + "2407fd27e8",
            Convert.ToHexString(data).ToLowerInvariant());
        // Put back as it was: the original block, byte for byte.
        Assert.True(device.SetEq(1, held));
        Assert.Equal(SettingsBlock, Convert.ToHexString(unit.Blocks[0x0004]).ToLowerInvariant());
    }

    [Fact]
    public void OlderFirmwareLeavesTheEqToSoftwareAndNamesThisModelsVersion()
    {
        var unit = new Unit();
        unit.Blocks[0x0000] = Unit.Identity(0, 2, 2);
        using var device = new Wave3Mk2Device(unit);
        device.Connect();
        Assert.False(device.Capabilities.HardwareEq);
        Assert.Equal(DeviceEqSupport.FirmwareOld, device.EqSupport!.Reason);
        Assert.Equal("0.2.3", device.EqSupport.MinimumFirmware);
        Assert.Contains("From 0.2.3", device.EqSupport.Note);
        Assert.Throws<InvalidOperationException>(() => device.SetEq(1, DeviceEq.Off));
        Assert.Empty(unit.Writes);
    }

    [Fact]
    public void TheDumpShowsTheBlocksAndTheFirmware()
    {
        using Wave3Mk2Device device = Connected(new Unit());
        IReadOnlyDictionary<string, string> dump = device.DumpBlocks();
        Assert.Equal(SettingsBlock, dump["settings"].ToLowerInvariant());
        Assert.Equal("0.2.3", dump["firmware"]);
        Assert.Equal("on the unit", dump["eqSupport"]);
        Assert.StartsWith("off;", dump["eq"]);
    }
}
