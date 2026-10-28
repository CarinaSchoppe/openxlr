namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    /// <summary>The block sizes a user can force, in frames.</summary>
    public static readonly IReadOnlyList<int> BlockSizes = [256, 512, 1024];

    private int? _blockSize;

    /// <summary>The forced block size in frames, or null when PipeWire chooses.</summary>
    public int? BlockSize { get { lock (_gate) return _blockSize; } }

    /// <summary>
    /// Force the graph OpenXLR runs in to <paramref name="frames"/> per
    /// cycle, one of <see cref="BlockSizes"/>, or hand the choice back to
    /// PipeWire with null. The meters carry the force, so it lasts while the
    /// graph is built and holds against other clients' requests. Save before
    /// applying; a failed save changes nothing. Machine setting, never part
    /// of a profile.
    /// </summary>
    public void SetBlockSize(int? frames, Func<MixerSettings, string?>? persist = null)
    {
        if (frames is int f && !BlockSizes.Contains(f))
            throw new ArgumentOutOfRangeException(nameof(frames), $"The block size must be one of {string.Join(", ", BlockSizes)} frames.");
        lock (_gate)
        {
            int? previous = _blockSize;
            _blockSize = frames;
            try { if (persist is not null) PersistLocked(persist); }
            catch { _blockSize = previous; throw; }
            if (previous != frames) _meters.SetForceQuantum(frames);
        }
    }
}
