using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using OpenXLR.UI;

namespace OpenXLR.Tests;

// WindowLayoutTests supplies the real Avalonia UI thread and xdg-config lock.
internal static class CompactPresentationWindowTests
{
    internal static void CheckBoundChoiceRollback()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-compact-choice-").FullName;
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
            foreach (bool initial in new[] { false, true })
            {
                new UiSettings { CompactMixer = initial }.SaveChecked();
                var client = new DaemonClient("ws://127.0.0.1:1/ws");
                try
                {
                    var vm = new MainViewModel(client);
                    var first = new ChannelViewModel(client, "first", "First", []);
                    var second = new ChannelViewModel(client, "second", "Second", []);
                    vm.Channels.Add(first);
                    vm.Channels.Add(second);
                    vm.SelectedCompactChannel = first;
                    var toggle = new ToggleButton { DataContext = vm };
                    var picker = new ComboBox { DataContext = vm, ItemsSource = vm.Channels };
                    using var toggleBinding = toggle.Bind(ToggleButton.IsCheckedProperty,
                        new Binding(nameof(MainViewModel.CompactMixer)) { Mode = BindingMode.TwoWay });
                    using var pickerBinding = picker.Bind(ComboBox.SelectedItemProperty,
                        new Binding(nameof(MainViewModel.SelectedCompactChannel)) { Mode = BindingMode.TwoWay });
                    string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
                    File.Move(path, path + ".saved");
                    Directory.CreateDirectory(path);
                    try
                    {
                        toggle.IsChecked = !initial;
                        picker.SelectedItem = second;
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(initial, vm.CompactMixer);
                        Assert.Same(first, vm.SelectedCompactChannel);
                        Assert.Contains("could not be saved", vm.Status);
                        Assert.True(toggle.IsChecked == initial && ReferenceEquals(first, picker.SelectedItem),
                            $"Rejected choices stayed in the controls: compact={toggle.IsChecked}, channel={(picker.SelectedItem as ChannelViewModel)?.Id}");
                    }
                    finally
                    {
                        Directory.Delete(path);
                        File.Move(path + ".saved", path);
                    }
                    toggle.IsChecked = !initial;
                    picker.SelectedItem = second;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(!initial, UiSettings.Load().CompactMixer);
                    Assert.True(UiSettings.Load().CompactChannel == "second",
                        $"Retry: vm={vm.SelectedCompactChannel?.Id}, picker={(picker.SelectedItem as ChannelViewModel)?.Id}, index={picker.SelectedIndex}, config={UiSettings.Load().CompactChannel}");
                }
                finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Directory.Delete(root, true);
        }
    }

}
