using System;

namespace OpenXLR.UI;

/// <summary>
/// Explicit desktop-style boost range. Incoming boosted state opens the range
/// before a bound slider sees the value, so it cannot coerce it back to 100%.
/// </summary>
public sealed class VolumeRangeViewModel(Action limitToUnity) : ViewModelBase
{
    private bool _boost;
    public bool Boost
    {
        get => _boost;
        set
        {
            if (!Set(ref _boost, value)) return;
            if (!value) limitToUnity();
            Raise(nameof(Maximum));
        }
    }

    public double Maximum => Boost ? 1.5 : 1;

    public void Include(double volume)
    {
        if (volume > 1) Boost = true;
    }
}
