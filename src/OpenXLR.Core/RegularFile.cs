using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Reject devices, sockets and FIFOs before an ordinary read can block.</summary>
internal static class RegularFile
{
    /// <summary>Read a regular file within one byte budget, including bytes added during the read.</summary>
    internal static byte[] ReadBytes(string path, int maximum)
    {
        Require(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > maximum) throw new IOException("File exceeds the inspection limit.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (bytes.Length + read > maximum) throw new IOException("File exceeds the inspection limit.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    /// <summary>Keep StreamReader's legacy BOM detection while bounding the file before decoding it.</summary>
    internal static string ReadText(string path, int maximum)
    {
        // Preserve the ordinary missing-file exceptions for optional stores.
        _ = File.GetAttributes(path);
        using var reader = new StreamReader(new MemoryStream(ReadBytes(path, maximum)));
        return reader.ReadToEnd();
    }

    internal static void Require(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        const uint type = 1; // STATX_TYPE
        LinuxFileStatus status;
        try
        {
            if (Statx(-100, path, 0, type, out status) != 0)
                throw new IOException($"Could not inspect the regular file {path}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        { throw new IOException($"Could not inspect the regular file {path}: statx is unavailable.", ex); }
        if ((status.Mask & type) == 0 || (status.Mode & 0xf000) != 0x8000)
            throw new IOException($"{path} is not a regular file.");
    }

    // Linux's fixed statx UAPI layout is the same on every supported architecture.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxFileStatus status);
}
