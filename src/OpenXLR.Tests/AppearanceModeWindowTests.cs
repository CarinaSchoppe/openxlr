using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>Runs on the real UI thread supplied by SkinWindowTests.</summary>
internal static class AppearanceModeWindowTests
{
    internal static void Check(MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        UiSettings saved = UiSettings.Load();
        string? overrideBefore = Environment.GetEnvironmentVariable(SkinService.OverrideVariable);
        ThemeVariant? requested = Application.Current!.RequestedThemeVariant;
        try
        {
            var preferences = System.Text.Json.JsonSerializer.SerializeToNode(saved,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();
            preferences["language"] = "zh-Hant";
            preferences["futureAppearance"] = new JsonObject { ["density"] = "touch" };
            OpenXLR.UI.OpenXlrPaths.WriteAtomic(Path.Combine(UiSettings.ConfigDir, "ui.json"), preferences.ToJsonString());
            SkinService.Choose("default");
            SkinService.ChooseMode(AppearanceModes.Dark);
            AssertLocalPreferences();
            Pump();
            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
            Assert.Equal(ThemeVariant.Dark, flow.ActualThemeVariant);
            Color dark = Background(main);
            Assert.Equal(Color.Parse("#16181d"), dark);
            Capture(main, "material-dark-main");
            Capture(options, "material-dark-options");
            Capture(flow, "material-dark-flow");

            var indicator = SkinService.LiveBrush("Ox.Led.On");
            SkinService.ChooseMode(AppearanceModes.Light);
            Pump();
            Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant);
            Assert.Equal(ThemeVariant.Light, flow.ActualThemeVariant);
            Assert.NotEqual(dark, Background(main));
            Assert.Equal(Color.Parse("#f3f5f8"), Background(main));
            Assert.Same(indicator, SkinService.LiveBrush("Ox.Led.On"));
            Assert.Equal(Color.Parse("#197b43"), indicator.Color);
            var picker = options.FindControl<ComboBox>("AppearanceModePicker")!;
            Assert.Equal(AppearanceModes.Light, Assert.IsType<AppearanceModeChoice>(picker.SelectedItem).Id);
            Assert.True(picker.IsEnabled);
            Capture(main, "material-light-main");
            Capture(options, "material-light-options");
            Capture(flow, "material-light-flow");

            SkinService.ChooseMode(AppearanceModes.System);
            string unchangedSettings = File.ReadAllText(Path.Combine(UiSettings.ConfigDir, "ui.json"));
            // Changing Avalonia's effective variant raises the same event as
            // the platform backend. No portal or user desktop is changed.
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
            Assert.Equal(dark, Background(main));
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            Pump();
            Assert.Equal(Color.Parse("#f3f5f8"), Background(main));
            Assert.Equal(unchangedSettings, File.ReadAllText(Path.Combine(UiSettings.ConfigDir, "ui.json")));

            foreach (SkinEntry entry in SkinCatalog.BuiltIn())
            {
                SkinService.ApplyPreference(AppearanceModes.Light, entry);
                IBrush? own = main.Background;
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                Pump();
                Assert.Same(own, main.Background);
                Assert.False(picker.IsEnabled);
            }

            SkinService.Choose("default");
            CheckProfileRecall(main);
            AssertLocalPreferences();
            CheckMalformedPreferences(options);
            CheckFailedSave(main, options);
            CheckReloadFallback(main);
            CheckLaunchOverride(main);
        }
        finally
        {
            saved.SaveChecked();
            Environment.SetEnvironmentVariable(SkinService.OverrideVariable, overrideBefore);
            SkinService.Initialize();
            Application.Current!.RequestedThemeVariant = requested;
            Pump();
        }
    }

    private static void AssertLocalPreferences()
    {
        var preferences = JsonNode.Parse(File.ReadAllText(Path.Combine(UiSettings.ConfigDir, "ui.json")))!;
        Assert.NotNull(preferences["language"]);
        Assert.Equal("zh-Hant", preferences["language"]!.GetValue<string>());
        Assert.Equal("touch", preferences["futureAppearance"]!["density"]!.GetValue<string>());
    }

    private static void CheckMalformedPreferences(OptionsWindow options)
    {
        string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
        string original = File.ReadAllText(path);
        var vm = (OptionsViewModel)options.DataContext!;
        var picker = options.FindControl<ComboBox>("AppearanceModePicker")!;
        try
        {
            foreach (string invalid in new[] { "null", "[]", "true", "{\"language\":\"de\",\"broken\":" })
            {
                OpenXLR.UI.OpenXlrPaths.WriteAtomic(path, invalid);
                picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Dark);
                Pump();
                Assert.Equal(invalid, File.ReadAllText(path));
                Assert.Equal(AppearanceModes.Light, SkinService.Mode);
                Assert.Equal(AppearanceModes.Light, vm.SelectedAppearanceMode!.Id);
                Assert.Equal(AppearanceModes.Light, Assert.IsType<AppearanceModeChoice>(picker.SelectedItem).Id);
                Assert.NotNull(vm.SkinError);
            }
        }
        finally { OpenXLR.UI.OpenXlrPaths.WriteAtomic(path, original); }
        picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Dark);
        Pump();
        Assert.Equal(AppearanceModes.Dark, UiSettings.Load().AppearanceMode);
        Assert.Null(vm.SkinError);
        AssertLocalPreferences();
        SkinService.ChooseMode(AppearanceModes.Light);
        Pump();
    }

    private static void CheckProfileRecall(MainWindow main)
    {
        var vm = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var recall = new JsonObject { ["revision"] = Guid.NewGuid().ToString("N"),
            ["settings"] = new JsonObject { ["appearanceMode"] = "dark" } };
        void Apply() => typeof(MainViewModel).GetMethod("ApplyProfilePresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [recall]);
        SkinService.ChooseMode(AppearanceModes.Light);
        Apply();
        Pump();
        Assert.Equal(AppearanceModes.Dark, SkinService.Mode);
        Assert.Equal(Color.Parse("#16181d"), Background(main));
        SkinService.ChooseMode(AppearanceModes.Light);
        Apply(); // A repeated state cannot undo the later manual choice.
        Assert.Equal(AppearanceModes.Light, SkinService.Mode);
        recall["revision"] = Guid.NewGuid().ToString("N");
        recall["settings"]!.AsObject().Remove("appearanceMode");
        Apply();
        Assert.Equal(AppearanceModes.Light, SkinService.Mode);
        Assert.Equal(AppearanceModes.Light, UiSettings.Load().AppearanceMode);
    }

    private static void CheckFailedSave(MainWindow main, OptionsWindow options)
    {
        string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
        File.Move(path, path + ".before-failure");
        Directory.CreateDirectory(path);
        try
        {
            var vm = (OptionsViewModel)options.DataContext!;
            var picker = options.FindControl<ComboBox>("AppearanceModePicker")!;
            picker.SelectedItem = vm.AppearanceModeChoices.Single(choice => choice.Id == AppearanceModes.Dark);
            Pump();
            Assert.Equal(AppearanceModes.Light, SkinService.Mode);
            Assert.Equal(AppearanceModes.Light, vm.SelectedAppearanceMode!.Id);
            Assert.Equal(AppearanceModes.Light, Assert.IsType<AppearanceModeChoice>(picker.SelectedItem).Id);
            Assert.Contains("could not be saved", vm.SkinError);
        }
        finally
        {
            Directory.Delete(path);
            File.Move(path + ".before-failure", path);
        }
        var retried = (OptionsViewModel)options.DataContext!;
        options.FindControl<ComboBox>("AppearanceModePicker")!.SelectedItem =
            retried.AppearanceModeChoices.Single(choice => choice.Id == AppearanceModes.Dark);
        Pump();
        Assert.Equal(AppearanceModes.Dark, SkinService.Mode);
        Assert.Equal(AppearanceModes.Dark, UiSettings.Load().AppearanceMode);
        SkinService.ChooseMode(AppearanceModes.Light);

        File.Move(path, path + ".before-failure");
        Directory.CreateDirectory(path);
        try
        {
            var picker = options.FindControl<ComboBox>("AppearanceModePicker")!;
            picker.SelectedItem = retried.AppearanceModeChoices.Single(choice => choice.Id == AppearanceModes.Dark);
            picker.SelectedItem = retried.AppearanceModeChoices.Single(choice => choice.Id == AppearanceModes.System);
        }
        finally
        {
            Directory.Delete(path);
            File.Move(path + ".before-failure", path);
        }
        // Recall before queued rejections run. They must preserve the new
        // profile mode and the selection refreshed by the skin service.
        var mainVm = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var recall = new JsonObject { ["revision"] = Guid.NewGuid().ToString("N"),
            ["settings"] = new JsonObject { ["appearanceMode"] = "dark", ["skin"] = "default" } };
        typeof(MainViewModel).GetMethod("ApplyProfilePresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mainVm, [recall]);
        Pump();
        Assert.Equal(AppearanceModes.Dark, SkinService.Mode);
        Assert.Equal(AppearanceModes.Dark, UiSettings.Load().AppearanceMode);
        Assert.Equal(AppearanceModes.Dark, retried.SelectedAppearanceMode!.Id);
        Assert.Equal(AppearanceModes.Dark, Assert.IsType<AppearanceModeChoice>(options.FindControl<ComboBox>("AppearanceModePicker")!.SelectedItem).Id);
        SkinService.ChooseMode(AppearanceModes.Light);
    }

    private static void CheckReloadFallback(MainWindow main)
    {
        string root = Directory.CreateTempSubdirectory("openxlr-mode-reload-").FullName;
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(root, "empty"));
            string folder = Path.Combine(SkinCatalog.UserSkinDir, "temporary");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "skin.json");
            foreach (string mode in new[] { AppearanceModes.Light, AppearanceModes.Dark })
            {
                File.WriteAllText(file, """{"schema":1,"name":"Temporary","tokens":{"Ox.Window.Background":"#123456"}}""");
                SkinService.ChooseMode(mode);
                SkinService.Choose("temporary");
                Application.Current!.RequestedThemeVariant = mode == AppearanceModes.Light ? ThemeVariant.Dark : ThemeVariant.Light;
                File.Delete(file);
                SkinService.Reload();
                Pump();
                Assert.Equal("default", SkinService.Current.Id);
                Assert.Equal(mode == AppearanceModes.Light ? ThemeVariant.Light : ThemeVariant.Dark,
                    Application.Current.RequestedThemeVariant);
                Assert.Equal(Color.Parse(mode == AppearanceModes.Light ? "#f3f5f8" : "#16181d"), Background(main));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            Directory.Delete(root, true);
            SkinService.Choose("default");
            SkinService.ChooseMode(AppearanceModes.Light);
        }
    }

    private static void CheckLaunchOverride(MainWindow main)
    {
        Environment.SetEnvironmentVariable(SkinService.OverrideVariable, "default");
        SkinService.Initialize();
        Assert.True(SkinService.Overridden);
        Assert.Equal(AppearanceModes.Light, UiSettings.Load().AppearanceMode);
        Assert.Equal(Color.Parse("#16181d"), Background(main));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Pump();
        Assert.Equal(Color.Parse("#16181d"), Background(main));
        var vm = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var recall = new JsonObject { ["revision"] = Guid.NewGuid().ToString("N"),
            ["settings"] = new JsonObject { ["appearanceMode"] = "light", ["skin"] = "default" } };
        typeof(MainViewModel).GetMethod("ApplyProfilePresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [recall]);
        Pump();
        Assert.True(SkinService.Overridden);
        Assert.Equal(Color.Parse("#16181d"), Background(main));
        string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
        File.Move(path, path + ".override-save");
        Directory.CreateDirectory(path);
        try
        {
            Exception? error = Record.Exception(() => SkinService.Choose("default"));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(SkinService.Overridden);
            Assert.Equal(Color.Parse("#16181d"), Background(main));
        }
        finally
        {
            Directory.Delete(path);
            File.Move(path + ".override-save", path);
        }
        SkinService.Choose("default");
        Assert.False(SkinService.Overridden);
        Assert.Equal(Color.Parse("#f3f5f8"), Background(main));
    }

    private static Color Background(Window window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_THEME_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
