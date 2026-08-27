namespace OpenXLR.Core.Devices;

/// <summary>
/// The Wave XLR MK.2 (0fd9:00b6): the vendor block protocol the Pro inherited,
/// at wIndex 0x0203 with single-input blocks.
///
///   read : bmRequestType=0xC1, bRequest=0x01, wValue=block, wIndex=0x0203
///   write: bmRequestType=0x41, bRequest=0x01, wValue=block, wIndex=0x0203
///
/// Block 0x0004 (38 bytes) is one input struct: gain dB at 0 (0..80), a flag
/// byte at 1 (bit0 mute, bit4 low cut, bit5 expander, bit6 voice tune), voice
/// tune strength at 10. Block 0x0005 (2 bytes): headphone attenuation at 0
/// (quarter-dB steps, 0 = loudest, 240 = -60 dB) and bit1 of byte 1 = low
/// impedance. Block 0x0001 (6 bytes): crossfade at 0 (0..200, 100 = centre).
/// Decoded from a Wave Link USB capture during the Pro reverse engineering;
/// unlike the Pro, no commit block is known to be required. Untested on MK.2
/// hardware so far: if a tester reports reads working but writes not sticking,
/// try following each write with the Pro's block 0x0003 commit.
/// </summary>
public sealed class WaveXlrMk2Device : IAudioDevice
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00B6;

    private const byte RtRead = 0xC1;
    private const byte RtWrite = 0x41;
    private const byte VReq = 0x01;
    private const ushort VIndex = 0x0203;

    private const ushort BlockCrossfade = 0x0001;
    private const ushort BlockSettings = 0x0004;
    private const ushort BlockHp = 0x0005;
    private const int CrossfadeLen = 6;
    private const int SettingsLen = 38;
    private const int HpLen = 2;

    private const byte MuteMask = 0x01;
    private const byte LowCutMask = 0x10;
    private const byte ExpanderMask = 0x20;
    private const byte VoiceTuneMask = 0x40;
    private const byte LowZMask = 0x02;

    private static IntPtr _ctx = IntPtr.Zero;
    private IntPtr _handle = IntPtr.Zero;
    private readonly object _lock = new();

    public DeviceInfo Info { get; } = new("Elgato", "Wave XLR MK.2", VendorId, ProductId);

    public DeviceCapabilities Capabilities { get; } = new()
    {
        Gain = true,
        PhysicalControls = true,
        Mute = true,
        LowCut = true,
        Expander = true,
        VoiceTune = true,
        HpVolume = true,
        LowImpedance = true,
        Crossfade = true,
        XlrInputs = 1,
        HpOutputs = 1,
    };

    public bool Connected => _handle != IntPtr.Zero;

    public void Connect()
    {
        if (_ctx == IntPtr.Zero)
        {
            int rc = LibUsb.libusb_init(out _ctx);
            if (rc != 0) throw new InvalidOperationException($"libusb_init failed: {LibUsb.StrError(rc)}");
        }
        _handle = LibUsb.libusb_open_device_with_vid_pid(_ctx, VendorId, ProductId);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Wave XLR MK.2 present but could not be opened (udev rule?)");
    }

    public void Disconnect()
    {
        if (_handle != IntPtr.Zero) { LibUsb.libusb_close(_handle); _handle = IntPtr.Zero; }
    }

    private byte[] Read(ushort block, int length)
    {
        var buf = new byte[length];
        lock (_lock)
        {
            int n = LibUsb.libusb_control_transfer(_handle, RtRead, VReq, block, VIndex, buf, (ushort)length, 1000);
            if (n < 0) throw new InvalidOperationException($"read block {block:x4}: {LibUsb.StrError(n)}");
            if (n != length) Array.Resize(ref buf, n);
        }
        return buf;
    }

    private void Write(ushort block, byte[] data)
    {
        lock (_lock)
        {
            int n = LibUsb.libusb_control_transfer(_handle, RtWrite, VReq, block, VIndex, data, (ushort)data.Length, 1000);
            if (n < 0) throw new InvalidOperationException($"write block {block:x4}: {LibUsb.StrError(n)}");
        }
    }

    private void Modify(ushort block, int length, Action<byte[]> edit)
    {
        byte[] b = Read(block, length);
        edit(b);
        Write(block, b);
    }

    public DeviceState ReadState()
    {
        byte[] s = Read(BlockSettings, SettingsLen);
        byte[] hp = Read(BlockHp, HpLen);
        byte[] xf = Read(BlockCrossfade, CrossfadeLen);
        return new DeviceState
        {
            GainDb = s[0],
            Mute = (s[1] & MuteMask) != 0,
            LowCut = (s[1] & LowCutMask) != 0,
            Expander = (s[1] & ExpanderMask) != 0,
            VoiceTune = (s[1] & VoiceTuneMask) != 0,
            VoiceTuneStrength = s[10],
            HpVolumeDb = -hp[0] / 4.0,
            LowImpedance = (hp[1] & LowZMask) != 0,
            Crossfade = xf[0],
        };
    }

    private void Flag(byte mask, bool on)
        => Modify(BlockSettings, SettingsLen, b => b[1] = on ? (byte)(b[1] | mask) : (byte)(b[1] & ~mask));

    public void SetGainDb(int db)
        => Modify(BlockSettings, SettingsLen, b => b[0] = (byte)Math.Clamp(db, 0, 80));

    public void SetMute(bool on) => Flag(MuteMask, on);
    public void SetLowCut(bool on) => Flag(LowCutMask, on);
    public void SetExpander(bool on) => Flag(ExpanderMask, on);
    public void SetVoiceTune(bool on) => Flag(VoiceTuneMask, on);

    public void SetVoiceTuneStrength(int value)
        => Modify(BlockSettings, SettingsLen, b => b[10] = (byte)Math.Clamp(value, 0, 100));

    public void SetHpVolumeDb(double db)
        => Modify(BlockHp, HpLen, b => b[0] = (byte)Math.Clamp((int)Math.Round(-db * 4), 0, 240));

    public void SetLowImpedance(bool on)
        => Modify(BlockHp, HpLen, b => b[1] = on ? (byte)(b[1] | LowZMask) : (byte)(b[1] & ~LowZMask));

    public void SetCrossfade(int value)
        => Modify(BlockCrossfade, CrossfadeLen, b => b[0] = (byte)Math.Clamp(value, 0, 200));

    // Not present on the MK.2.
    public void SetPhantom(bool on) { }
    public void SetClipGuard(bool on) { }
    public void SetCompressor(bool on) { }

    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        foreach ((string name, ushort block, int len) in new[]
                 { ("settings", BlockSettings, SettingsLen), ("hp", BlockHp, HpLen),
                   ("crossfade", BlockCrossfade, CrossfadeLen) })
        {
            try { blocks[name] = Convert.ToHexString(Read(block, len)); }
            catch (Exception ex) { blocks[name] = $"error: {ex.Message}"; }
        }
        return blocks;
    }
}
