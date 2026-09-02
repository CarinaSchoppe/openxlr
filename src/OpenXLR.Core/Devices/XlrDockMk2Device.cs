namespace OpenXLR.Core.Devices;

/// <summary>
/// The Elgato Wave XLR Dock MK.2 (0fd9:00c7), the Stream Deck+ module built
/// on the same Wave FX platform as the Wave XLR MK.2. Its USB descriptor is
/// interface-for-interface identical to the MK.2's (reported in issue #1), so
/// it is driven through the MK.2 vendor block protocol at wIndex 0x0203.
/// Like the first XLR Dock it has no physical controls of its own; the Stream
/// Deck+ dials drive it through software. Not yet run on hardware.
/// </summary>
public sealed class XlrDockMk2Device : WaveXlrMk2Device
{
    public new const ushort ProductId = 0x00C7;

    public XlrDockMk2Device() : base(ProductId, "Wave XLR Dock MK.2", physicalControls: false) { }
}
