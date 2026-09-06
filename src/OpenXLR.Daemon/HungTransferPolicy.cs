namespace OpenXLR.Daemon;

/// <summary>
/// What to do with a device whose USB transfers keep hanging. Each hung
/// transfer abandons a native handle and a parked thread (see LibUsb), so
/// reconnecting for ever leaks them for ever; after <see cref="Limit"/>
/// hangs in one run the device is set aside: the daemon stops driving it,
/// keeps the mixer and any other interface alive, and says so in the
/// state. Unplugging the device and plugging it back in (its firmware
/// restarts) gives it a fresh count, but only up to <see cref="LifetimeLimit"/>
/// hangs in one daemon run: every abandoned transfer stays in the process
/// until the daemon restarts, so past that point only a restart helps.
/// </summary>
public sealed class HungTransferPolicy
{
    public const int Limit = 3;
    public const int LifetimeLimit = 9;

    private readonly Dictionary<ushort, int> _hung = [];
    private readonly Dictionary<ushort, int> _lifetime = [];
    private readonly HashSet<ushort> _setAside = [];

    /// <summary>Record a hung transfer; true when this one crossed the limit.</summary>
    public bool NoteHung(ushort productId)
    {
        int n = _hung.GetValueOrDefault(productId) + 1;
        _hung[productId] = n;
        _lifetime[productId] = _lifetime.GetValueOrDefault(productId) + 1;
        if (n < Limit) return false;
        _setAside.Add(productId);
        return true;
    }

    public int HungCount(ushort productId) => _hung.GetValueOrDefault(productId);

    public bool IsSetAside(ushort productId) => _setAside.Contains(productId);

    /// <summary>Whether the device has used up its hangs for this daemon run; only a restart drives it again.</summary>
    public bool IsSpentForThisRun(ushort productId) => _lifetime.GetValueOrDefault(productId) >= LifetimeLimit;

    /// <summary>
    /// The device left the bus and came back: its firmware restarted, so it
    /// gets a fresh count. True when it may be driven again; false when its
    /// hangs for this run are used up and it stays set aside.
    /// </summary>
    public bool Returned(ushort productId)
    {
        _hung.Remove(productId);
        if (IsSpentForThisRun(productId)) return false;
        _setAside.Remove(productId);
        return true;
    }

    public IEnumerable<ushort> SetAside => _setAside;
}
