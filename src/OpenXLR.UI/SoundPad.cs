using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// A soundboard pad: a key that plays its clip when it goes down, by the
/// pointer, Space or Enter, and sends the release when it comes up, which
/// ends a hold clip. A key held down plays once, however long the keyboard
/// repeats. It wears the button face the skin chose, since it is a button
/// to everything that styles one.
/// </summary>
public sealed class SoundPad : Button
{
    private bool _down;

    protected override Type StyleKeyOverride => typeof(Button);

    private SoundClipViewModel? SoundClip => DataContext as SoundClipViewModel;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Down();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Up();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Up();
    }

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

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Up();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>A screen reader's press: down and straight up again.</summary>
    protected override void OnClick()
    {
        base.OnClick();
        if (_down) return;
        Down();
        Up();
    }

    private void Down()
    {
        if (_down || SoundClip is not { } clip) return;
        _down = true;
        clip.Board.Press(clip);
    }

    private void Up()
    {
        if (!_down) return;
        _down = false;
        if (SoundClip is { } clip) clip.Board.Release(clip);
    }
}
