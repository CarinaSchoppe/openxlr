using System;
using System.Linq;
using System.Threading.Tasks;
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

    private async void OnProfileSave(object? sender, RoutedEventArgs e)
    {
        string name = ProfileNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        bool exists = _vm.Profiles.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (exists && !await ConfirmAsync("Overwrite profile?",
                $"A profile named \"{name}\" already exists for this device.\n" +
                "Saving will replace it with the current scene."))
            return;
        _vm.SaveProfile(name);
        ProfileNameBox.Text = "";
    }

    /// <summary>Small in-app confirmation dialog; true when the user accepts.</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = new Button { Content = "Overwrite", Background = Avalonia.Media.Brush.Parse("#a03434") };
        var no = new Button { Content = "Cancel" };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Avalonia.Media.Brush.Parse("#1d2027"),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { no, yes },
                    },
                },
            },
        };
        var done = new TaskCompletionSource<bool>();
        yes.Click += (_, _) => { done.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { done.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => done.TrySetResult(false);
        await dialog.ShowDialog(this);
        return await done.Task;
    }

    private void OnProfileLoad(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Content is string name) _vm.LoadProfile(name);
    }

    private void OnProfileDelete(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string name) _vm.DeleteProfile(name);
    }

    private void OnPickDevice(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DetectedDeviceItem d) _vm.SelectDevice(d);
    }

    private FlowWindow? _flow;

    private void OnFlow(object? sender, RoutedEventArgs e)
    {
        // Non-modal so it can sit on another monitor while mixing; one at a time.
        if (_flow is { IsVisible: true }) { _flow.Activate(); return; }
        _flow = new FlowWindow(_vm);
        _flow.Show(this);
    }
}
