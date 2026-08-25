using System.Runtime.InteropServices;

namespace OpenXLR.Core;

/// <summary>
/// Minimal P/Invoke over libusb-1.0, just enough for the Wave XLR Pro's vendor
/// control transfers. Mirrors the validated Python/ctypes prototype rather than
/// depending on a higher-level wrapper for the critical path. The vendor
/// interface (3) is unclaimed by any kernel driver, so no detach is needed.
/// </summary>
internal static class LibUsb
{
    private const string Lib = "libusb-1.0.so.0";

    [DllImport(Lib)] internal static extern int libusb_init(out IntPtr ctx);
    [DllImport(Lib)] internal static extern void libusb_exit(IntPtr ctx);

    [DllImport(Lib)]
    internal static extern IntPtr libusb_open_device_with_vid_pid(
        IntPtr ctx, ushort vendorId, ushort productId);

    [DllImport(Lib)] internal static extern void libusb_close(IntPtr devHandle);

    [DllImport(Lib)]
    internal static extern int libusb_control_transfer(
        IntPtr devHandle, byte bmRequestType, byte bRequest,
        ushort wValue, ushort wIndex,
        byte[] data, ushort wLength, uint timeout);

    [DllImport(Lib)] internal static extern IntPtr libusb_strerror(int errcode);

    internal static string StrError(int code)
    {
        IntPtr p = libusb_strerror(code);
        return Marshal.PtrToStringAnsi(p) ?? $"libusb error {code}";
    }
}
