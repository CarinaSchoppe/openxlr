using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static class MiniLiveWindowTests
{
    // Runs on the existing fixture's actual UI thread, with private preferences.
    internal static void Check()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-mini-window-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        MainWindow? window = null;
        OptionsWindow? options = null;
        WaveInterfacesWindow? waveWindow = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var main = (MainViewModel)window.DataContext!;
            // Use the window's client for all interactive controls.
            var client = (DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var a = new MixViewModel(client, "monitor", "Monitor A");
            var b = new MixViewModel(client, "monitorb", "Monitor B");
            var chat = new MixViewModel(client, "chat", "Chat");
            main.Mixes.Add(a); main.Mixes.Add(b); main.Mixes.Add(chat);
            main.Channels.Add(new ChannelViewModel(client, "music", "Music with a long display name", ["monitor", "monitorb", "chat"]));
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [JsonNode.Parse("""{"mixes":[{"id":"monitor","name":"Monitor A"},{"id":"monitorb","name":"Monitor B"},{"id":"chat","name":"Chat","kind":"virtualMic"}],"channels":[{"id":"music","name":"Music with a long display name","levels":{"monitor":0.5,"monitorb":0.7,"chat":0.4}}]}""")]);
            window.Show();
            Layout(window, 1040, 900);
            main.MiniView = true;
            Layout(window, 520, 720);
            main.SelectedCompactMix = b;
            Layout(window, 460, 640);
            Assert.Equal(460, window.MinWidth);
            Assert.Equal(460, window.ClientSize.Width);
            Capture(window, "mini-en");
            Assert.Single(main.Mixes, m => m.DisplayVisible);
            Assert.Equal("monitorb", main.SelectedCompactMix!.Id);
            Assert.All(window.GetVisualDescendants().OfType<Button>().Where(c => c.IsEffectivelyVisible && c is not RepeatButton), c =>
                Assert.True(c.Bounds.Width >= 20, $"Collapsed button: {c.Content}, {c.Bounds}"));
            var output = window.FindControl<Slider>("OutputVolumeSlider")!;
            Assert.True(output.Bounds.Width > 80, $"Output slider disappeared: {output.Bounds}");
            var optionsModel = new OptionsViewModel(client, main);
            options = new OptionsWindow(optionsModel); options.Show(window);
            optionsModel.SelectedLanguage = optionsModel.LanguageChoices.First(c => c.Id == "de");
            Layout(window, 460, 640); options.UpdateLayout();
            Assert.Equal("de", Localizer.Language);
            Capture(window, "mini-de");
            Assert.Equal("Mini-Ansicht", window.FindControl<ToggleButton>("MiniViewButton")!.Content);
            var direct = options.FindControl<CheckBox>("DirectNativeEditor")!;
            Assert.Equal("Native Plugin-Oberflächen direkt öffnen", ((TextBlock)direct.Content!).Text);
            optionsModel.SelectedLanguage = optionsModel.LanguageChoices.First(c => c.Id == "ar");
            Layout(window, 460, 640);
            Assert.Equal(Localizer.Text("MiniView"), window.FindControl<ToggleButton>("MiniViewButton")!.Content);
            Assert.Equal("monitorb", main.SelectedCompactMix.Id);
            Capture(window, "mini-ar");
            var mixPicker = window.FindControl<ComboBox>("CompactMixPicker")!;
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            string saved = File.ReadAllText(path); File.Delete(path); Directory.CreateDirectory(path);
            try
            {
                optionsModel.SelectedLanguage = optionsModel.LanguageChoices.First(c => c.Id == "fr");
                direct.IsChecked = false;
                mixPicker.SelectedItem = chat;
                Dispatcher.UIThread.RunJobs();
                Assert.Same(b, main.SelectedCompactMix); Assert.Same(b, mixPicker.SelectedItem);
                Assert.Equal("ar", Localizer.Language);
                Assert.Equal("ar", optionsModel.SelectedLanguage!.Id);
                Assert.True(optionsModel.OpenNativeEditorDirectly); Assert.True(direct.IsChecked);
                Assert.NotNull(optionsModel.LanguageError); Assert.NotNull(optionsModel.NativeEditorPreferenceError);
            }
            finally { Directory.Delete(path); File.WriteAllText(path, saved); }
            direct.IsChecked = false; mixPicker.SelectedItem = chat; Dispatcher.UIThread.RunJobs();
            Assert.Same(chat, main.SelectedCompactMix); Assert.Equal("chat", UiSettings.Load().CompactMix);
            Assert.False(UiSettings.Load().OpenNativeEditorDirectly);
            optionsModel.SelectedLanguage = optionsModel.LanguageChoices.First(c => c.Id == "en");
            Layout(window, 460, 640);
            Assert.Equal("Mini view", window.FindControl<ToggleButton>("MiniViewButton")!.Content);
            options.Close(); options = null;
            foreach (var skin in SkinCatalog.Discover())
            {
                SkinService.Apply(skin);
                Layout(window, 460, 640);
                Assert.True(output.Bounds.Width > 80);
            }
            var additional = new WaveInterfaceViewModel(client, main, "0fd9:00b4@0123456789abcdef");
            additional.Apply(JsonNode.Parse("""
                {"name":"An additional Wave XLR Pro with a long device label", "connected":true,"enabled":true,
                 "capabilities":{"gain":true,"mute":true,"phantom":true,"lowCut":true,"clipGuard":true,"xlrInputs":2},
                 "state":{"gainDb":42,"gain2Db":36}}
                """)!);
            main.WaveInterfaces.Add(additional);
            waveWindow = new WaveInterfacesWindow(main); waveWindow.Show(window); Layout(waveWindow, 480, 620);
            var gainSliders = waveWindow.GetVisualDescendants().OfType<Slider>().Where(c => c.IsEffectivelyVisible).ToArray();
            Assert.Equal(2, gainSliders.Length);
            Assert.All(gainSliders, slider => Assert.True(slider.Bounds.Width > 80));
            Capture(waveWindow, "additional-wave-480");
            waveWindow.Close(); waveWindow = null;
            main.MiniView = false; Layout(window, 1040, 900);
            Layout(window, 640, 900);
            Assert.Equal(640, window.MinWidth);
            Capture(window, "full-640");
            Assert.Equal(3, main.Mixes.Count(m => m.DisplayVisible));
            window.Quit(); window = null;
        }
        finally
        {
            waveWindow?.Close(); options?.Close(); window?.Quit();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Localizer.Initialize(); Localizer.ApplyResources();
            SkinService.ApplyPreference(UiSettings.Load().AppearanceMode, SkinCatalog.Find(UiSettings.Load().Skin) ?? new SkinEntry(SkinPackage.Default, []));
            Directory.Delete(dir, true);
        }
    }

}
