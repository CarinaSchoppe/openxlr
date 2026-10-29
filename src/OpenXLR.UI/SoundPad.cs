using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// A soundboard pad: a key that plays its clip when it goes down, by the
/// mouse, a pen, Space or Enter, and sends the release when it comes up,
/// which ends a hold clip. A key held down plays once, however long the
/// keyboard repeats. A finger plays on lifting instead, and only when it
/// did not move, so a touch scroll that starts on a pad plays nothing; a
/// hold pad keeps playing on the finger's touch, since its clip lasts as
/// long as the finger stays. Whatever ends a press, the pad going away,
/// moving, turning off or its window losing focus, releases the clip the
/// press started, by the id it had then. It wears the button face the skin
/// chose, since it is a button to everything that styles one.
/// </summary>
public sealed class SoundPad : Button
{
    /// <summary>How far a finger may travel, in device-independent units, and still play the pad on lifting.</summary>
    internal const double TouchSlop = 10;

    // The clip a press started, held until that press ends.
    private SoundClipViewModel? _held;
    // A finger on a pad that plays on lifting, and where it came down.
    private Point? _touchStart;

    protected override Type StyleKeyOverride => typeof(Button);

    private SoundClipViewModel? SoundClip => DataContext as SoundClipViewModel;

    /// <summary>The pad is down: a press started its clip and has not ended.</summary>
    internal bool IsHeld => _held is not null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed) PointerDown(e.Pointer.Type, point.Position);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_touchStart is not null) PointerMove(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        PointerUp(e.GetPosition(this));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // A scroller that takes the finger over for a scroll ends the touch without a play.
        _touchStart = null;
        Up();
    }

    /// <summary>A pointer came down at <paramref name="at"/>: play now, or, for a finger, wait for it to lift.</summary>
    internal void PointerDown(PointerType type, Point at)
    {
        if (type == PointerType.Touch && SoundClip is { IsHold: false })
        {
            _touchStart = at;
            return;
        }
        Down();
    }

    /// <summary>A finger that travels past the slop is a scroll, not a press.</summary>
    internal void PointerMove(Point at)
    {
        if (_touchStart is { } start && Distance(start, at) > TouchSlop) _touchStart = null;
    }

    /// <summary>The pointer came up: a finger that stayed put plays the pad now; any press ends.</summary>
    internal void PointerUp(Point at)
    {
        if (_touchStart is { } start)
        {
            _touchStart = null;
            if (Distance(start, at) <= TouchSlop && new Rect(Bounds.Size).Contains(at))
            {
                Down();
                Up();
            }
            return;
        }
        Up();
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Down();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Up();
            e.Handled = true;
            return;
        }
        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Up();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A pad that turns off mid-press gets no key or pointer up of its own.
        if (change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled)
        {
            _touchStart = null;
            Up();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _touchStart = null;
        Up();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>A screen reader's press: down and straight up again, so a hold clip ends at once.</summary>
    protected override void OnClick()
    {
        base.OnClick();
        if (_held is not null || _touchStart is not null) return;
        Down();
        Up();
    }

    /// <summary>End the press, wherever it came from: the window lost focus, or the pad went away.</summary>
    internal void ReleaseHeld()
    {
        _touchStart = null;
        Up();
    }

    private void Down()
    {
        if (_held is not null || SoundClip is not { } clip) return;
        _held = clip;
        clip.Board.Press(clip);
    }

    private void Up()
    {
        if (_held is not { } clip) return;
        _held = null;
        clip.Board.Release(clip);
    }
}
