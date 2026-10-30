namespace OpenXLR.Core.Devices;

/// <summary>
/// The Elgato Wave:3 MK.2 (0fd9:00ca): a USB condenser microphone with a
/// headphone jack, built on the Wave FX platform of the Wave XLR MK.2 rather
/// than on the first Wave:3's class-request dialect (<see cref="Wave3Device"/>).
/// Its USB descriptor has the MK.2's interface layout (three audio
/// interfaces, vendor interface 3, DFU interface 4) and it answers the vendor
/// block protocol at wIndex 0x0103 (0x0203 stalls) with the MK.2's block
/// layout: gain in dB at block 0x0004 byte 0, mute at bit 0 of byte 1, the
/// headphone attenuation in quarter-dB steps at block 0x0005 byte 0 and the
/// direct monitor crossfade at block 0x0001 byte 0, which runs the other way
/// from the XLR MK.2's: 0 is the computer alone and 200 the microphone alone,
/// as the maintainer heard on 2026-10-30 after the crossfade had been mapped
/// straight. Block 0x0003 stalls, so
/// there is no commit block, as on the MK.2s.
///
/// Mapped on the maintainer's unit (bcdDevice 2.20, build
/// 2025-12-02-14:53-ZP-Release) by moving the physical controls while the
/// blocks were read, then each control written through this class and read
/// back from the block and, for the gain and the headphones, from the
/// kernel's ALSA controls ('Capture Volume' 0..40 dB, 'Playback Volume'
/// 0..240 for -60..0 dB), which the firmware keeps in step with the block.
///
/// The processing has the XLR MK.2's layout but for the low cut, mapped on
/// the same unit on 2026-10-30 by switching one setting at a time and
/// comparing the microphone's own room noise before and after (median of
/// three alternating takes, the expander held off): clearing bit 0x02 of the
/// flag byte raised 20 to 100 Hz by 9 to 13 dB and left the rest flat, so
/// 0x02, phantom power on the XLR MK.2, is the low cut here, and the XLR
/// MK.2's low cut bit 0x10 changed nothing. Setting 0x80 (compressor) raised
/// the noise by 5.5 dB, as a compressor's make-up gain does; clearing 0x40
/// (voice tune) lowered it by 2.8 dB; and byte 10, voice tune's strength and
/// the value one of the dial's modes moves, raised it by 11.4 dB from 16 to
/// 100. The expander (0x20) and ClipGuard (block byte 2, bit 0x04 set for
/// off) showed no clear change on steady room noise, which neither acts on,
/// and are driven at the XLR MK.2's offsets. Phantom power and the low
/// impedance mode are not advertised, and bit 0x10 is never written.
///
/// The unit runs the MK.2's four-band EQ (<see cref="WaveFxEq"/>) at the same
/// offsets of block 0x0004, and came with the same factory bands as a Wave
/// XLR MK.2. Measured on the maintainer's unit with the microphone's own
/// noise as the signal and one band on at a time, written through this class:
/// a +12 dB bell at 1 kHz with Q 1 raised the spectrum by 12.2 dB at 1 kHz
/// and within 1.3 dB of the RBJ cookbook curve from 250 Hz to 4 kHz (median
/// of four takes), a low pass at 1 kHz cut 4, 8 and 16 kHz by about 24, 37
/// and 56 dB, and a high pass at 1 kHz cut 63, 125 and 250 Hz by about 39,
/// 31 and 19 dB. Block 0x0000 bytes 5 to 7 read 0.2.3 on that unit: the
/// firmware version in this model's own numbering, which does not follow the
/// XLR MK.2's 1.0.x, so the EQ gate is the version it was measured on
/// (<see cref="EqFirmware"/>) rather than the XLR MK.2's 1.0.15.
///
/// Both capture channels carry the same mono signal, bit for bit, so the
/// strip takes the pair as the Wave:3's is taken. The unit enumerates at high
/// speed and still has its own form of the full-duplex ordering bug: when
/// its playback opened first, its capture stopped in the kernel or delivered
/// a steady buzz, and a USB reset followed by capture before playback cleared
/// it. 53-openxlr-wave3-capture-hold.conf (packaging/) matches its capture
/// node too. The microphone also stalled whenever the headphones played in
/// the two-channel S32_LE format PipeWire picks, so
/// 55-openxlr-wave3-mk2-playback.conf opens them in the unit's other format,
/// FLOAT_LE on three channels, the format its capture runs in.
///
/// Whether it keeps its settings across a power cycle has not been tested.
/// It is taken to, as the Wave XLR MK.2 and the XLR Dock MK.2 do, so the
/// daemon writes nothing at connect that the user did not ask for.
/// </summary>
public sealed class Wave3Mk2Device : WaveXlrMk2Device
{
    public new const ushort ProductId = 0x00CA;

    /// <summary>The highest gain, in dB: the kernel's capture control range.</summary>
    internal const int GainMaxDb = 40;

    public Wave3Mk2Device() : this(null) { }

    internal Wave3Mk2Device(IUsbTransport? usb) : base(ProductId, "Wave:3 MK.2", vIndex: 0x0103, Controls, EqFirmware, usb: usb,
        crossfadeReversed: true, lowCutMask: LowCutMask) { }

    /// <summary>The flag byte's low cut bit on this model, phantom power's on the XLR MK.2.</summary>
    internal const byte LowCutMask = 0x02;

    /// <summary>The firmware the EQ was measured on, in this model's own numbering.</summary>
    internal static Version EqFirmware { get; } = new(0, 2, 3);

    private static readonly DeviceCapabilities Controls = new()
    {
        Gain = true,
        Mute = true,
        HpVolume = true,
        Crossfade = true,
        LowCut = true,
        Expander = true,
        VoiceTune = true,
        Compressor = true,
        ClipGuard = true,
        // A dial with four modes and a touch mute, as on the Wave:3, so the
        // daemon's gain lock is kept off the UI.
        PhysicalControls = true,
        RetainsSettings = true,
        XlrInputs = 1,   // the capsule feeds the one input strip
        XlrJacks = false,   // a capsule, not a jack
        HpOutputs = 1,
        GainMaxDb = GainMaxDb,
        // Its capture stalls under its own playback; a USB reset clears it.
        CaptureRecovery = true,
    };
}
