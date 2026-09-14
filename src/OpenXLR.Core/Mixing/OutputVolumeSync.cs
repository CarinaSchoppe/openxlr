namespace OpenXLR.Core.Mixing;

/// <summary>
/// Volume writes to the secondary monitor outputs that have not landed.
///
/// The selected outputs are held at one volume, so a change on the first of
/// them is copied to the others. A device that is asleep, re-enumerating or
/// unplugged refuses that write, and the mixer would otherwise never come
/// back to it: the first output does not move again, so there is nothing left
/// to compare against and the device stays at whatever level it woke up with.
///
/// What is owed to each output is kept here instead. A write that failed is
/// tried again on the next few sweeps, and the attempts are renewed when the
/// monitor route to that output is rebuilt, which is what a device coming
/// back looks like. Only failures are kept, so outputs that took their value
/// are never written twice; a newer value replaces an older one, and outputs
/// that leave the selection, or a selection that starts from a fresh volume
/// baseline, are forgotten rather than written later from a stale desire.
/// </summary>
internal sealed class OutputVolumeSync
{
    /// <summary>Sweeps a failed write is retried for before it waits for the device to return.</summary>
    internal const int Attempts = 3;

    private sealed class Owed
    {
        public double Volume;
        public int Left;
    }

    private readonly Dictionary<string, Owed> _owed = new(StringComparer.Ordinal);

    /// <summary>The write landed; nothing is owed to this output any more.</summary>
    public void Delivered(string sink) => _owed.Remove(sink);

    /// <summary>The write failed. A different value starts its retries again.</summary>
    public void Failed(string sink, double volume)
    {
        if (_owed.TryGetValue(sink, out Owed? owed) && owed.Volume.Equals(volume)) owed.Left--;
        else _owed[sink] = new Owed { Volume = volume, Left = Attempts };
    }

    /// <summary>The route to this output was rebuilt: the device is back, so try again.</summary>
    public void Rearm(string sink)
    {
        if (_owed.TryGetValue(sink, out Owed? owed)) owed.Left = Attempts;
    }

    /// <summary>Forget everything owed to outputs that are no longer selected.</summary>
    public void Keep(IEnumerable<string> sinks)
    {
        var selected = new HashSet<string>(sinks, StringComparer.Ordinal);
        foreach (string sink in _owed.Keys.Where(s => !selected.Contains(s)).ToList())
            _owed.Remove(sink);
    }

    /// <summary>Forget everything: a fresh baseline owes no output anything.</summary>
    public void Clear() => _owed.Clear();

    /// <summary>What to write again now, in a stable order.</summary>
    public IReadOnlyList<(string Sink, double Volume)> Due()
        => [.. _owed.Where(e => e.Value.Left > 0).OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => (e.Key, e.Value.Volume))];
}
