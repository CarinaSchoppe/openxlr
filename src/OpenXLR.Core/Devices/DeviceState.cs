namespace OpenXLR.Core.Devices;

/// <summary>
/// A full snapshot of the Wave XLR Pro's controllable state, as read from the
/// vendor blocks. Gain is expressed directly in dB (0..80), matching the
/// hardware byte and the ALSA capture-volume control.
/// </summary>
public sealed record DeviceState
{
    public int GainDb { get; init; }            // block 0x0004 off0, 0..80 dB
    public bool Mute { get; init; }             // block 0x0004 off1 bit0
    public bool LowCut { get; init; }           // block 0x0004 off1 bit4
    public bool Expander { get; init; }         // block 0x0004 off1 bit5
    public bool VoiceTune { get; init; }        // block 0x0004 off1 bit6
    public int VoiceTuneStrength { get; init; } // block 0x0004 off10, 0..100

    public double HpVolumeDb { get; init; }     // block 0x0005 off0, dB = -byte/4
    public double Hp2VolumeDb { get; init; }    // block 0x0005 off2 (second headphone out)
    public bool LowImpedance { get; init; }     // block 0x0005 off1 bit1

    public int Crossfade { get; init; }         // block 0x0001 off0, 0..200 (100 = centre)

    // Pro-only mic DSP. Bit positions are PROVISIONAL (block 0x0004 off1 bits
    // 1/3/7): the capture + Windows config corroborate phantom and ClipGuard,
    // but the polarity bit is unconfirmed. Finalize by ear once the UI exists.
    public bool Phantom { get; init; }
    public bool ClipGuard { get; init; }
    public bool Polarity { get; init; }

    // Second XLR input (the Pro has XLR 1 and XLR 2; block 0x0004 holds one
    // 38-byte structure per input, XLR 2 at offset 38, same field layout).
    public int Gain2Db { get; init; }
    public bool Mute2 { get; init; }
    public bool LowCut2 { get; init; }
    public bool Expander2 { get; init; }
    public bool VoiceTune2 { get; init; }
    public int VoiceTuneStrength2 { get; init; }
    public bool Phantom2 { get; init; }
    public bool ClipGuard2 { get; init; }
    public bool Polarity2 { get; init; }
}
