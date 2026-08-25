namespace OpenXLR.Core.Devices;

/// <summary>
/// Discovers which supported audio interfaces are attached and constructs the
/// matching <see cref="IAudioDevice"/>. New brands are added by registering a
/// (VID, PID) -> factory entry, so the daemon, UI, and plugin need no changes.
///
/// Detection reads sysfs only (/sys/bus/usb/devices), so it works without USB
/// permissions or the udev rule; opening the device for control still needs it.
/// </summary>
public static class DeviceRegistry
{
    private readonly record struct Key(ushort Vid, ushort Pid);

    private static readonly Dictionary<Key, Func<IAudioDevice>> Factories = new()
    {
        [new Key(WaveXlrProDevice.VendorId, WaveXlrProDevice.ProductId)] = () => new WaveXlrProDevice(),
        // Add more brands/models here, e.g.:
        // [new Key(0x0FD9, 0x00B6)] = () => new WaveXlrMk2Device(),
        // [new Key(0x1220, 0x8fe0)] = () => new GoXlrDevice(),
    };

    /// <summary>Every supported device currently attached, newest API first is not guaranteed.</summary>
    public static IReadOnlyList<IAudioDevice> DetectAll()
    {
        var found = new List<IAudioDevice>();
        foreach (var (vid, pid) in EnumerateUsbIds())
            if (Factories.TryGetValue(new Key(vid, pid), out var make))
                found.Add(make());
        return found;
    }

    /// <summary>The first supported device attached, or null.</summary>
    public static IAudioDevice? DetectFirst() => DetectAll().Count > 0 ? DetectAll()[0] : null;

    private static IEnumerable<(ushort Vid, ushort Pid)> EnumerateUsbIds()
    {
        string root = "/sys/bus/usb/devices";
        if (!Directory.Exists(root)) yield break;
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string vidPath = Path.Combine(dir, "idVendor");
            string pidPath = Path.Combine(dir, "idProduct");
            if (!File.Exists(vidPath) || !File.Exists(pidPath)) continue;

            ushort vid, pid;
            try
            {
                vid = Convert.ToUInt16(File.ReadAllText(vidPath).Trim(), 16);
                pid = Convert.ToUInt16(File.ReadAllText(pidPath).Trim(), 16);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IOException)
            {
                continue;
            }
            yield return (vid, pid);
        }
    }
}
