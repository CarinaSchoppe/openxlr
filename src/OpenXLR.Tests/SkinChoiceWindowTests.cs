using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

// WindowLayoutTests supplies the real Avalonia UI thread and xdg-config lock.
internal static class SkinChoiceWindowTests
{
    internal static void CheckFailedChoiceCanBeRetried()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-skin-choice-").FullName;
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var current = SkinService.Current;
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
            new UiSettings { Skin = current.Id }.Save();
            var options = new OptionsViewModel(client, new MainViewModel(client));
            var before = options.SelectedSkin;
            var after = options.SkinChoices.First(choice => choice.Id != before!.Id);
            var picker = new ComboBox { DataContext = options, ItemsSource = options.SkinChoices };
            using var binding = picker.Bind(ComboBox.SelectedItemProperty,
                new Binding(nameof(OptionsViewModel.SelectedSkin)) { Mode = BindingMode.TwoWay });
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            File.Move(path, path + ".saved");
            Directory.CreateDirectory(path);
            try
            {
                picker.SelectedItem = after;
                Dispatcher.UIThread.RunJobs();
                Assert.Same(before, options.SelectedSkin);
                Assert.Same(before, picker.SelectedItem);
                Assert.Equal(current.Id, SkinService.Current.Id);
                Assert.Contains("could not be saved", options.SkinError);
            }
            finally
            {
                Directory.Delete(path);
                File.Move(path + ".saved", path);
            }
            Assert.Equal(current.Id, UiSettings.Load().Skin);
            picker.SelectedItem = after;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(after.Id, SkinService.Current.Id);
            Assert.Equal(after.Id, UiSettings.Load().Skin);
            Assert.Same(after, options.SelectedSkin);
            Assert.Same(after, picker.SelectedItem);
            Assert.Null(options.SkinError);
        }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SkinService.Apply(current);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Directory.Delete(root, true);
        }
    }
}
