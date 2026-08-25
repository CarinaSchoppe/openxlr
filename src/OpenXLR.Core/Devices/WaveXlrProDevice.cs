namespace OpenXLR.Core.Devices;

/// <summary>
/// Wave XLR Pro (0fd9:00b4) hardware control over the vendor block protocol.
///
/// Transport (decoded from a Wave Link USB capture, verified on hardware):
///   read : bmRequestType=0xC1, bRequest=0x01, wValue=block, wIndex=0x0103
///   write: bmRequestType=0x41, bRequest=0x01, wValue=block, wIndex=0x0103, data
/// Each block is a fixed-size property bank; individual controls are byte fields
/// at fixed offsets. Writes are read-modify-write of the whole block. Needs the
/// udev rule granting access to 0fd9:00b4 (MODE 0660 / uaccess).
/// </summary>
public sealed class WaveXlrProDevice : IAudioDevice, IDisposable
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00B4;

    public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", VendorId, ProductId);

    public DeviceCapabilities Capabilities { get; } = new()
    {
        Gain = true, Mute = true, LowCut = true, Expander = true, VoiceTune = true,
        HpVolume = true, LowImpedance = true, Crossfade = true,
        Phantom = true, ClipGuard = true, Polarity = true,
        XlrInputs = 2, HpOutputs = 2,
    };

    private const ushort VIndex = 0x0103;
    private const byte VReq = 0x01;
    private const byte RtRead = 0xC1;
    private const byte RtWrite = 0x41;

    // Block numbers (wValue) and their lengths.
    private const ushort BlockCrossfade = 0x0001;
    private const int CrossfadeLen = 108;
    private const ushort BlockSettings = 0x0004;
    private const int SettingsLen = 80;
    private const ushort BlockHp = 0x0005;
    private const int HpLen = 8;

    // Field offsets within the blocks. Block 0x0004 carries one 38-byte
    // structure per XLR input (verified bidirectionally against ALSA: off0
    // tracks the front pair, off38 the rear pair); XLR 2 fields are the same
    // offsets shifted by Xlr2Base. The 4-byte tail (off76) is a third input
    // stage, not yet exposed.
    private const int Xlr2Base = 38;
    private const int GainOffset = 0;      // block 0x0004
    private const int FlagsOffset = 1;     // block 0x0004 / 0x0005
    private const int VoiceTuneStrengthOffset = 10; // block 0x0004
    private const int HpVolOffset = 0;     // block 0x0005 (headphones 1)
    private const int Hp2VolOffset = 2;    // block 0x0005 (headphones 2)
    private const int CrossfadeOffset = 0; // block 0x0001

    // Flag masks in block 0x0004 offset 1.
    private const byte MuteMask = 0x01;      // bit0 (confirmed vs ALSA)
    private const byte LowCutMask = 0x10;    // bit4
    private const byte ExpanderMask = 0x20;  // bit5
    private const byte VoiceTuneMask = 0x40; // bit6
    // Pro-only, PROVISIONAL bit positions. Confirm by ear.
    private const byte PhantomMask = 0x02;   // bit1
    private const byte ClipGuardMask = 0x08; // bit3
    private const byte PolarityMask = 0x80;  // bit7

    // Flag mask in block 0x0005 offset 1.
    private const byte LowZMask = 0x02;      // bit1

    public const int GainMaxDb = 80;
    public const int VoiceTuneStrengthMax = 100;
    public const int CrossfadeMax = 200;

    private IntPtr _ctx;
    private IntPtr _handle;
    private readonly object _lock = new();

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
            throw new InvalidOperationException(
                "Wave XLR Pro not found or no permission (install the udev rule for 0fd9:00b4).");
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
            if (n < 0) throw new IOException($"vendor read block {block:X4} failed: {LibUsb.StrError(n)}");
            if (n != length) Array.Resize(ref buf, n);
        }
        return buf;
    }

    private void Write(ushort block, byte[] data)
    {
        lock (_lock)
        {
            int n = LibUsb.libusb_control_transfer(_handle, RtWrite, VReq, block, VIndex, data, (ushort)data.Length, 1000);
            if (n < 0) throw new IOException($"vendor write block {block:X4} failed: {LibUsb.StrError(n)}");
        }
    }

    /// <summary>Read every control field into one snapshot.</summary>
    public DeviceState ReadState()
    {
        byte[] b1 = Read(BlockCrossfade, CrossfadeLen);
        byte[] b4 = Read(BlockSettings, SettingsLen);
        byte[] b5 = Read(BlockHp, HpLen);
        byte f = b4[FlagsOffset];
        byte f2 = b4[Xlr2Base + FlagsOffset];
        return new DeviceState
        {
            GainDb = b4[GainOffset],
            Mute = (f & MuteMask) != 0,
            LowCut = (f & LowCutMask) != 0,
            Expander = (f & ExpanderMask) != 0,
            VoiceTune = (f & VoiceTuneMask) != 0,
            VoiceTuneStrength = b4[VoiceTuneStrengthOffset],
            HpVolumeDb = -b5[HpVolOffset] / 4.0,
            Hp2VolumeDb = -b5[Hp2VolOffset] / 4.0,
            LowImpedance = (b5[FlagsOffset] & LowZMask) != 0,
            Crossfade = b1[CrossfadeOffset],
            Phantom = (f & PhantomMask) != 0,
            ClipGuard = (f & ClipGuardMask) != 0,
            Polarity = (f & PolarityMask) != 0,
            Gain2Db = b4[Xlr2Base + GainOffset],
            Mute2 = (f2 & MuteMask) != 0,
            LowCut2 = (f2 & LowCutMask) != 0,
            Expander2 = (f2 & ExpanderMask) != 0,
            VoiceTune2 = (f2 & VoiceTuneMask) != 0,
            VoiceTuneStrength2 = b4[Xlr2Base + VoiceTuneStrengthOffset],
            Phantom2 = (f2 & PhantomMask) != 0,
            ClipGuard2 = (f2 & ClipGuardMask) != 0,
            Polarity2 = (f2 & PolarityMask) != 0,
        };
    }

    // --- setters (read-modify-write a single field) ---

    public void SetGainDb(int db)
    {
        db = Math.Clamp(db, 0, GainMaxDb);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[GainOffset] = (byte)db;
        Write(BlockSettings, b);
    }

    public void SetVoiceTuneStrength(int value)
    {
        value = Math.Clamp(value, 0, VoiceTuneStrengthMax);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[VoiceTuneStrengthOffset] = (byte)value;
        Write(BlockSettings, b);
    }

    public void SetCrossfade(int value)
    {
        value = Math.Clamp(value, 0, CrossfadeMax);
        byte[] b = Read(BlockCrossfade, CrossfadeLen);
        b[CrossfadeOffset] = (byte)value;
        Write(BlockCrossfade, b);
    }

    /// <summary>Set HP volume in dB; the hardware byte is -4*dB (0 = loudest).</summary>
    public void SetHpVolumeDb(double db)
    {
        int byte0 = Math.Clamp((int)Math.Round(-db * 4.0), 0, 240);
        byte[] b = Read(BlockHp, HpLen);
        b[HpVolOffset] = (byte)byte0;
        Write(BlockHp, b);
    }

    /// <summary>Second headphone output volume; same -4*dB byte encoding.</summary>
    public void SetHp2VolumeDb(double db)
    {
        int byte0 = Math.Clamp((int)Math.Round(-db * 4.0), 0, 240);
        byte[] b = Read(BlockHp, HpLen);
        b[Hp2VolOffset] = (byte)byte0;
        Write(BlockHp, b);
    }

    public void SetGain2Db(int db)
    {
        db = Math.Clamp(db, 0, GainMaxDb);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[Xlr2Base + GainOffset] = (byte)db;
        Write(BlockSettings, b);
    }

    public void SetVoiceTuneStrength2(int value)
    {
        value = Math.Clamp(value, 0, VoiceTuneStrengthMax);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[Xlr2Base + VoiceTuneStrengthOffset] = (byte)value;
        Write(BlockSettings, b);
    }

    public void SetMute2(bool on) => SetSettingsBit2(MuteMask, on);
    public void SetLowCut2(bool on) => SetSettingsBit2(LowCutMask, on);
    public void SetExpander2(bool on) => SetSettingsBit2(ExpanderMask, on);
    public void SetVoiceTune2(bool on) => SetSettingsBit2(VoiceTuneMask, on);
    public void SetPhantom2(bool on) => SetSettingsBit2(PhantomMask, on);
    public void SetClipGuard2(bool on) => SetSettingsBit2(ClipGuardMask, on);
    public void SetPolarity2(bool on) => SetSettingsBit2(PolarityMask, on);

    private void SetSettingsBit2(byte mask, bool on)
    {
        byte[] b = Read(BlockSettings, SettingsLen);
        int off = Xlr2Base + FlagsOffset;
        b[off] = (byte)(on ? b[off] | mask : b[off] & ~mask);
        Write(BlockSettings, b);
    }

    public void SetMute(bool on) => SetSettingsBit(MuteMask, on);
    public void SetLowCut(bool on) => SetSettingsBit(LowCutMask, on);
    public void SetExpander(bool on) => SetSettingsBit(ExpanderMask, on);
    public void SetVoiceTune(bool on) => SetSettingsBit(VoiceTuneMask, on);
    public void SetPhantom(bool on) => SetSettingsBit(PhantomMask, on);
    public void SetClipGuard(bool on) => SetSettingsBit(ClipGuardMask, on);
    public void SetPolarity(bool on) => SetSettingsBit(PolarityMask, on);

    public void SetLowImpedance(bool on)
    {
        byte[] b = Read(BlockHp, HpLen);
        b[FlagsOffset] = (byte)(on ? b[FlagsOffset] | LowZMask : b[FlagsOffset] & ~LowZMask);
        Write(BlockHp, b);
    }

    private void SetSettingsBit(byte mask, bool on)
    {
        byte[] b = Read(BlockSettings, SettingsLen);
        b[FlagsOffset] = (byte)(on ? b[FlagsOffset] | mask : b[FlagsOffset] & ~mask);
        Write(BlockSettings, b);
    }

    public void Dispose()
    {
        Disconnect();
        if (_ctx != IntPtr.Zero) { LibUsb.libusb_exit(_ctx); _ctx = IntPtr.Zero; }
    }
}
