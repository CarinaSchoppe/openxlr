using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class MainWindow
{
    private MixerWindow? _mixerWindow;

    private void OnPopOutMixer(object? sender, RoutedEventArgs e) => PopOutMixer();

    internal MixerWindow PopOutMixer()
    {
        if (_mixerWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        // A drag belongs to its original window and cannot survive moving the panel.
        CancelReorder();
        ArrangeButton.IsChecked = false;
        OnArrange(this, new RoutedEventArgs());
        var panel = (Control)MixerHost.Content!;
        // Keep inherited bindings alive while the panel has no parent.
        panel.DataContext = DataContext;
        MixerHost.Content = null;
        var window = new MixerWindow();
        _mixerWindow = window;
        window.Attach(panel, DataContext, this);
        bool restoreMain = true;
        window.Closing += (_, e) => restoreMain = e.CloseReason == WindowCloseReason.WindowClosing;
        window.Closed += (_, _) =>
        {
            MixerHost.Content = window.Detach();
            panel.ClearValue(DataContextProperty);
            _mixerWindow = null;
            DetachedMixerNotice.IsVisible = false;
            PopOutMixerButton.IsVisible = true;
            if (!_reallyExit && restoreMain) ShowMixer();
        };
        DetachedMixerNotice.IsVisible = true;
        PopOutMixerButton.IsVisible = false;
        window.Show();
        return window;
    }

    private void OnDockMixer(object? sender, RoutedEventArgs e) => _mixerWindow?.Close();
}
