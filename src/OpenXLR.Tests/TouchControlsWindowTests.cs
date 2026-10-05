using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

// SkinWindowTests supplies populated windows, the UI thread and isolated paths.
internal static class TouchControlsWindowTests
{
    internal static void Check(MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        if (Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") is string scale)
            Assert.Equal(double.Parse(scale, System.Globalization.CultureInfo.InvariantCulture), main.RenderScaling, 3);
        UiSettings saved = UiSettings.Load();
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        double width = main.Width, height = main.Height;
        options.Hide();
        flow.Hide();
        try
        {
            var picker = options.FindControl<ComboBox>("ControlSizingPicker")!;
            var vm = (OptionsViewModel)options.DataContext!;
            var standard = vm.ControlSizingChoices.Single(c => !c.Touch);
            var large = vm.ControlSizingChoices.Single(c => c.Touch);
            SkinService.ApplyControlSizing(false);
            CheckClassStylePrecedence(main);
            foreach (SkinEntry entry in SkinCatalog.Discover().Where(s => s.Package.Origin == SkinOrigin.BuiltIn))
            {
                SkinService.Apply(entry);
                Layout(main, 1040, 1000);
                Assert.Equal(30, main.FindControl<Slider>("OutputVolumeSlider")!.Bounds.Height);
                DragFader(main, main.FindControl<Slider>("OutputVolumeSlider")!, largeTarget: false);
                if (entry.Id == "default")
                    foreach (double baselineWidth in new[] { 640d, 1800d })
                    {
                        Layout(main, baselineWidth, 1000);
                        Capture(main, "standard-main-" + baselineWidth);
                    }
                object background = Application.Current!.Resources["Ox.Window.Background"]!;
                double level = main.FindControl<Slider>("OutputVolumeSlider")!.Value;
                picker.SelectedItem = large;
                Pump(main);
                Assert.True(UiSettings.Load().TouchControls);
                Assert.Equal(level, main.FindControl<Slider>("OutputVolumeSlider")!.Value);
                Assert.Same(background, Application.Current.Resources["Ox.Window.Background"]);
                foreach (double size in new[] { 640d, 1800d })
                {
                    Layout(main, size, 1000);
                    CheckTargets(main);
                    if (entry.Id == "default" && size == 640) Capture(main, "touch-main-narrow");
                }
                var slider = main.FindControl<Slider>("OutputVolumeSlider")!;
                DragFader(main, slider);
                CheckPopup(main);
                foreach (Slider send in main.GetVisualDescendants().OfType<Slider>()
                    .Where(s => s.DataContext is SendViewModel or MixViewModel)
                    .GroupBy(s => s.DataContext!.GetType()).Select(g => g.First()))
                    CheckThumbCorners(main, send);
                level = slider.Value;
                if (entry.Id == "default") Capture(main, "touch-main");
                picker.SelectedItem = standard;
                Pump(main);
                Assert.Equal(30, slider.Bounds.Height);
                Assert.Equal(level, slider.Value);
                Assert.False(UiSettings.Load().TouchControls);
            }
            SkinService.Apply(new(SkinPackage.Default, []));
            CheckFailedSave(main, vm, picker, standard, large);
            CheckProfiles(main, vm);
            CheckLargerSkin(main);
            SkinService.Apply(new(SkinPackage.Default, []));
            SkinService.ApplyControlSizing(false);
            Layout(main, 1040, 1000);
            Capture(main, "standard-main");
        }
        finally
        {
            saved.SaveChecked();
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Layout(main, width, height);
            options.Show();
            flow.Show();
        }
    }

    private static void CheckClassStylePrecedence(MainWindow main)
    {
        var button = main.FindControl<Button>("OptionsButton")!;
        var style = new Style(s => s.OfType<Button>().Class("small-target-probe"));
        style.Setters.Add(new Setter(Button.MinHeightProperty, 24d));
        style.Setters.Add(new Setter(Button.MinWidthProperty, 24d));
        main.Styles.Insert(0, style);
        button.Classes.Add("small-target-probe");
        try
        {
            Pump(main);
            Assert.Equal(24, button.MinHeight);
            SkinService.ApplyControlSizing(true);
            Pump(main);
            Assert.True(button.Bounds.Width >= 44 && button.Bounds.Height >= 44,
                $"Touch must override earlier compact class styles: {button.Bounds.Size}.");
            SkinService.ApplyControlSizing(false);
            Pump(main);
            Assert.Equal(24, button.MinHeight);
            Assert.Equal(24, button.MinWidth);
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            button.Classes.Remove("small-target-probe");
            main.Styles.Remove(style);
            Pump(main);
        }
    }

    private static void CheckTargets(MainWindow main)
    {
        var sliders = main.GetVisualDescendants().OfType<Slider>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Width > 0).ToArray();
        Assert.NotEmpty(sliders);
        foreach (var slider in sliders)
        {
            Assert.True(slider.Bounds.Height >= 44, $"slider height {slider.Bounds.Height}");
            Thumb thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
            Assert.True(thumb.Bounds.Width >= 44 && thumb.Bounds.Height >= 44,
                $"thumb target {thumb.Bounds.Size}");
            Assert.True(thumb.Bounds.Height <= slider.Bounds.Height);
        }
        var output = main.FindControl<Slider>("OutputVolumeSlider")!;
        var model = (MainViewModel)main.DataContext!;
        double previous = model.OutputVolume;
        bool boost = model.OutputVolumeRange.Boost;
        foreach (double level in new[] { 1d, 1.5 })
        {
            model.OutputVolume = level;
            Pump(main);
            var label = ((Grid)output.Parent!).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 5);
            Assert.Equal(level == 1 ? "100%" : "150%", label.Text);
            Assert.True(label.DesiredSize.Width <= label.Bounds.Width + .1,
                $"The monitor percentage must fit: desired {label.DesiredSize.Width}, actual {label.Bounds.Width}.");
        }
        model.OutputVolume = previous;
        model.OutputVolumeRange.Boost = boost;
        Pump(main);
        var inserts = main.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Classes.Contains("insertmini") && c.IsEffectivelyVisible).ToArray();
        Assert.NotEmpty(inserts);
        foreach (Control control in inserts)
        {
            Assert.True(control.Bounds.Width >= 44 && control.Bounds.Height >= 44,
                $"insert target {control.Bounds.Size}");
            var row = Assert.IsType<Grid>(control.Parent);
            Assert.True(control.Bounds.Right <= row.Bounds.Width + .1,
                $"insert action escaped its row: {control.Bounds}, row {row.Bounds.Size}");
        }
    }

    private static void CheckPopup(MainWindow main)
    {
        ComboBox combo = main.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.IsEffectivelyVisible && c.Items.Count > 1);
        combo.BringIntoView();
        Pump(main);
        combo.IsDropDownOpen = true;
        WaitForInput();
        try
        {
            Popup popup = combo.GetVisualDescendants().OfType<Popup>().Single();
            var rows = popup.Child!.GetVisualDescendants().OfType<ComboBoxItem>().ToArray();
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.True(row.Bounds.Height >= 44, $"popup row {row.Bounds.Height}"));
        }
        finally { combo.IsDropDownOpen = false; Pump(main); }
    }

    private static Thumb CheckThumbCorners(MainWindow main, Slider slider)
    {
        slider.BringIntoView(new Rect(-4, -32, slider.Bounds.Width + 8, slider.Bounds.Height + 64));
        WaitForInput();
        main.UpdateLayout();
        WaitForInput();
        Thumb thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        Point[] corners = [new(3, 3), new(thumb.Bounds.Width - 3, 3),
            new(3, thumb.Bounds.Height - 3), new(thumb.Bounds.Width - 3, thumb.Bounds.Height - 3)];
        bool HitsThumb(Point corner)
        {
            Point edge = thumb.TranslatePoint(corner, main)!.Value;
            var hit = main.InputHitTest(edge) as Visual;
            return hit == thumb || hit?.GetVisualAncestors().Contains(thumb) == true;
        }
        // Hit testing follows the committed render tree. At fractional scale,
        // scrolling and arranging can finish before that tree catches up.
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!corners.All(HitsThumb) && wait.Elapsed < TimeSpan.FromSeconds(2))
        {
            WaitForInput();
            main.UpdateLayout();
        }
        Assert.True(corners.All(HitsThumb),
            $"All four enlarged thumb corners must receive input; skin={SkinService.Current.Id}, " +
            $"data={slider.DataContext?.GetType().Name}, thumb={thumb.Bounds}.");
        return thumb;
    }

    private static void DragFader(MainWindow main, Slider slider, bool largeTarget = true)
    {
        main.Activate();
        slider.BringIntoView();
        WaitForInput();
        Thumb thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        if (largeTarget) CheckThumbCorners(main, slider);
        using var pointer = new XPointer();
        pointer.Focus(main.TryGetPlatformHandle()!.Handle);
        slider.Value = .4;
        WaitForInput();
        PixelPoint focusPoint = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
        pointer.MoveTo(focusPoint.X, focusPoint.Y);
        WaitForInput();
        pointer.Click();
        WaitForInput();
        slider.Focus();
        WaitForInput();
        Assert.True(slider.IsKeyboardFocusWithin, "The native input test needs slider focus.");
        slider.Value = .4;
        WaitForInput();
        Key? received = null;
        void OnKey(object? sender, KeyEventArgs args) => received = args.Key;
        slider.AddHandler(InputElement.KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        pointer.Key(0xff53); // Right
        WaitForInput();
        slider.RemoveHandler(InputElement.KeyDownEvent, OnKey);
        Assert.True(slider.Value > .4, $"The fader (touch={largeTarget}) must retain keyboard control: value {slider.Value}, key {received}, enabled {slider.IsEffectivelyEnabled}, maximum {slider.Maximum}.");
        slider.Value = .4;
        WaitForInput();
        PixelPoint point = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
        double before = slider.Value;
        pointer.MoveTo(point.X, point.Y);
        WaitForInput();
        pointer.SetButton(true);
        WaitForInput();
        pointer.MoveTo(point.X + 60, point.Y);
        WaitForInput();
        pointer.SetButton(false);
        WaitForInput();
        Assert.True(slider.Value > before, $"Touch drag did not change {before} to a higher value: {slider.Value}");
    }

    private static void WaitForInput()
    {
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(70));
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    private static void CheckFailedSave(MainWindow main, OptionsViewModel vm, ComboBox picker,
        ControlSizingChoice standard, ControlSizingChoice large)
    {
        string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
        File.Move(path, path + ".saved");
        Directory.CreateDirectory(path);
        try
        {
            picker.SelectedItem = large;
            Pump(main);
            Assert.False(SkinService.TouchControls);
            Assert.Same(standard, picker.SelectedItem);
            Assert.Same(standard, vm.SelectedControlSizing);
            Assert.Contains("could not be saved", vm.SkinError);
        }
        finally { Directory.Delete(path); File.Move(path + ".saved", path); }
        picker.SelectedItem = large;
        Pump(main);
        Assert.True(SkinService.TouchControls);
        Assert.True(UiSettings.Load().TouchControls);
        Assert.Same(large, picker.SelectedItem);
        Assert.Null(vm.SkinError);
    }

    private static void CheckProfiles(MainWindow main, OptionsViewModel options)
    {
        var vm = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        void Recall(JsonObject settings)
        {
            var recall = new JsonObject { ["revision"] = Guid.NewGuid().ToString("N"), ["settings"] = settings };
            typeof(MainViewModel).GetMethod("ApplyProfilePresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [recall]);
            Pump(main);
        }
        Recall(new());
        Assert.True(SkinService.TouchControls);
        Recall(new() { ["touchControls"] = false });
        Assert.False(SkinService.TouchControls);
        Assert.False(options.SelectedControlSizing!.Touch);
        Recall(new() { ["touchControls"] = true });
        Assert.True(SkinService.TouchControls);
        Assert.True(options.SelectedControlSizing!.Touch);
    }

    private static void CheckLargerSkin(MainWindow main)
    {
        var targetOnly = SkinPackage.Default with { Tokens = new Dictionary<string, SkinValue>
        {
            ["Ox.Mixer.ControlMinSize"] = new SkinNumber(64),
        } };
        foreach (bool touch in new[] { true, false })
        {
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(new(targetOnly, []));
            Layout(main, 1040, 1000);
            var targetSlider = main.FindControl<Slider>("OutputVolumeSlider")!;
            var targetThumb = Assert.Single(targetSlider.GetVisualDescendants().OfType<Thumb>());
            Assert.Equal(64, targetThumb.Bounds.Height);
            Assert.True(targetSlider.Bounds.Height >= targetThumb.Bounds.Height);
            CheckThumbCorners(main, targetSlider);
        }
        SkinService.ApplyControlSizing(true);
        var custom = SkinPackage.Default with { Tokens = new Dictionary<string, SkinValue>
        {
            ["Ox.Mixer.ControlMinSize"] = new SkinNumber(60),
            ["Ox.Mixer.InsertControlMinSize"] = new SkinNumber(60),
            ["Ox.Mixer.ChannelWidth"] = new SkinNumber(320),
            ["Ox.Mixer.MixWidth"] = new SkinNumber(400),
            ["Ox.Fader.Thumb.Width"] = new SkinNumber(64),
            ["Ox.Fader.Thumb.Height"] = new SkinNumber(64),
        } };
        SkinService.Apply(new(custom, []));
        Layout(main, 1040, 1000);
        Slider slider = main.FindControl<Slider>("OutputVolumeSlider")!;
        Assert.True(slider.Bounds.Height >= 64);
        var thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        Assert.Equal(64, thumb.Bounds.Width);
        Assert.Equal(64, thumb.Bounds.Height);
        Assert.Equal(320d, Application.Current!.Resources["Ox.Mixer.ChannelWidth"]);
        Assert.Equal(400d, Application.Current.Resources["Ox.Mixer.MixWidth"]);
        CheckTargets(main);
        CheckThumbCorners(main, slider);
        Capture(main, "touch-custom-large");
        SkinService.ApplyControlSizing(false);
        Assert.Equal(60d, Application.Current.Resources["Ox.Mixer.ControlMinSize"]);
        Assert.Equal(320d, Application.Current.Resources["Ox.Mixer.ChannelWidth"]);
    }

    private static void Layout(Window window, double width, double height)
    {
        window.PlatformImpl!.GetType().GetMethod("Resize", [typeof(Size), typeof(WindowResizeReason)])!
            .Invoke(window.PlatformImpl, [new Size(width, height), WindowResizeReason.User]);
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(60));
        Dispatcher.UIThread.MainLoop(stop.Token);
        Pump(window);
    }

    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TOUCH_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        if (window.Content is ScrollViewer scroll) { scroll.Offset = default; Pump(window); }
        using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        image.Render(window);
        image.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
