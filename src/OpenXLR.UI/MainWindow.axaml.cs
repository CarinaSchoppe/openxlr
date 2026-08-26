using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace OpenXLR.UI;

public partial class MainWindow : Window
{
    private readonly DaemonClient _client = new();
    private readonly MainViewModel _vm;
    private TrayIcon? _tray;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(_client);
        DataContext = _vm;
        _client.Start();          // connects, and keeps retrying if the daemon isn't up yet
        HeaderVersion.Text = $"v{AppVersion.Current}";
        SetupTray();

        // Start hidden in the tray when configured (and a tray actually
        // exists; otherwise the window must show or nothing is reachable).
        if (UiSettings.Load().StartMinimized && _tray is not null)
        {
            bool hidden = false;
            Opened += (_, _) => { if (!hidden) { hidden = true; Hide(); } };
        }

        Closing += (_, e) =>
        {
            // With minimize-to-tray on, the close button hides the window; the
            // tray menu's Quit (or disabling the option) exits for real. Only a
            // user-initiated window close is intercepted: cancelling an
            // OS/application shutdown request here blocks the whole system
            // from logging out or rebooting.
            if (_vm.MinimizeToTray && !_reallyExit &&
                e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += async (_, _) =>
        {
            _tray?.Dispose();
            await _client.DisposeAsync();
        };
    }

    private void SetupTray()
    {
        try
        {
            var menu = new NativeMenu();
            var show = new NativeMenuItem("Show mixer");
            show.Click += (_, _) => { Show(); Activate(); };
            var quit = new NativeMenuItem("Quit OpenXLR");
            quit.Click += (_, _) => { _reallyExit = true; Close(); };
            menu.Items.Add(show);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OpenXLR.UI/Assets/icon.png"))),
                ToolTipText = "OpenXLR",
                Menu = menu,
            };
            _tray.Clicked += (_, _) => { Show(); Activate(); };
        }
        catch (Exception)
        {
            // No tray host available: the option simply has no effect.
            _tray = null;
        }
    }

    private void OnOptions(object? sender, RoutedEventArgs e)
        => new OptionsWindow(new OptionsViewModel(_client, _vm)).ShowDialog(this);

    private void OnManageApps(object? sender, RoutedEventArgs e)
        => new AppsWindow { DataContext = _vm }.ShowDialog(this);

    private void OnAbout(object? sender, RoutedEventArgs e)
        => new AboutWindow().ShowDialog(this);

    private FlowWindow? _flow;

    private void OnFlow(object? sender, RoutedEventArgs e)
    {
        // Non-modal so it can sit on another monitor while mixing; one at a time.
        if (_flow is { IsVisible: true }) { _flow.Activate(); return; }
        _flow = new FlowWindow(_vm);
        _flow.Show(this);
    }
}
