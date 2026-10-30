using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// The Voice changer window, opened from the Voice changer button in the
/// SUBMIXER header. One is open at a time: a second request raises it.
/// </summary>
public partial class VoiceChangerWindow : Window
{
    private static VoiceChangerWindow? _open;

    public VoiceChangerWindow()
    {
        InitializeComponent();
        // Touch sizing, or a skin's own control minimum, enlarges the targets here as in the mixer.
        UpdateControlSizing();
        Skinning.SkinService.Changed += UpdateControlSizing;
        Closed += (_, _) => Skinning.SkinService.Changed -= UpdateControlSizing;
    }

    private void UpdateControlSizing() => Classes.Set("large-targets", Skinning.SkinService.LargeTargets);

    private VoiceChangerViewModel? Changer => DataContext as VoiceChangerViewModel;

    /// <summary>The open window, for the tests.</summary>
    internal static VoiceChangerWindow? Current => _open;

    /// <summary>
    /// Open the window centred over <paramref name="near"/>, or in the middle of the screen when it
    /// is hidden. The window has no owner, as the Soundboard window has none: an owned window is
    /// minimized with the mixer, and the window manager raises the mixer each time a tile is pressed.
    /// </summary>
    public static VoiceChangerWindow Open(Window? near, VoiceChangerViewModel changer)
    {
        if (_open is { } open)
        {
            if (open.DataContext == changer)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return open;
            }
            open.Close();
        }
        var window = new VoiceChangerWindow { DataContext = changer };
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        _open = window;
        if (near is { IsVisible: true } && near.WindowState != WindowState.Minimized)
        {
            double scale = near.DesktopScaling;
            Size frame = near.FrameSize ?? near.ClientSize;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(
                near.Position.X + (int)((frame.Width - window.Width) * scale / 2),
                near.Position.Y + (int)((frame.Height - window.Height) * scale / 2));
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        return window;
    }

    private void OnTile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is VoiceTileViewModel tile) Changer?.Choose(tile);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
