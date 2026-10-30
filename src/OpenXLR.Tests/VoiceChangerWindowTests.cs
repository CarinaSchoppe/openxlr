using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Voice changer window: the Voice changer button stands in the SUBMIXER
/// header right after Soundboard, lights while a microphone has a voice on and
/// opens the one window, which has no owner; the window offers the
/// microphones, an Off tile and a tile per voice, sends a press as one
/// command, shows the custom voice's sliders only for Custom, and fits at its
/// size and at its narrowest in the font <c>OPENXLR_LAYOUT_FONT</c> names.
/// Run from the window layout test, on its UI thread. <see cref="CheckSkins"/>
/// runs from the skin test: the header button and the tiles in the skins'
/// own faces.
/// </summary>
internal static class VoiceChangerWindowTests
{
    private sealed class Recorder : IVoiceCommands
    {
        public readonly ConcurrentQueue<(string Channel, bool? Enabled, string? Voice)> Sent = new();

        public Task SetVoiceChangerAsync(string channel, bool? enabled = null, string? voice = null, double? pitch = null, double? formant = null)
        {
            Sent.Enqueue((channel, enabled, voice));
            return Task.CompletedTask;
        }
    }

    /// <summary>A Wave XLR Pro's state with the soundboard and XLR 1 reporting its voice changer.</summary>
    private static JsonNode State(bool enabled, string voice = "robot")
    {
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(""))!;
        state["type"] = "state";
        var board = new JsonObject
        {
            ["channel"] = "sfx", ["enabled"] = true, ["available"] = true, ["error"] = null, ["formats"] = new JsonArray("wav"),
            ["clips"] = new JsonArray(), ["playing"] = new JsonArray(), ["auditioning"] = null, ["pages"] = new JsonArray(),
        };
        JsonObject mixer = SoundboardWindowTests.Mixer(true, board);
        var xlr1 = (JsonObject)mixer["channels"]![0]!;
        xlr1["microphone"] = true;
        xlr1["voice"] = new JsonObject
        {
            ["enabled"] = enabled, ["voice"] = voice, ["pitch"] = 0, ["formant"] = 0,
            ["active"] = enabled ? voice : null, ["available"] = true, ["error"] = null,
        };
        state["mixer"] = mixer;
        // Through text, as the socket hands it over: numbers then read as any number type.
        return JsonNode.Parse(state.ToJsonString())!;
    }

    /// <summary>
    /// The SUBMIXER header's Voice changer button: shown, right after
    /// Soundboard in the row and on screen, labelled, inside the window and
    /// clear of the header's other controls.
    /// </summary>
    internal static Button AssertHeaderButton(Window window, string where)
    {
        Button button = Find<Button>(window, "VoiceChangerButton");
        Button sounds = Find<Button>(window, "SoundboardButton");
        var row = (WrapPanel)button.Parent!;
        Assert.Same(row, sounds.Parent);
        Assert.True(row.Children.IndexOf(button) == row.Children.IndexOf(sounds) + 1, $"{where}: the Voice changer button does not follow Soundboard.");
        Assert.True(button.IsEffectivelyVisible, $"{where}: the header has no Voice changer button.");
        Assert.Equal(Localizer.Text("VoiceChanger"), button.Content);
        Assert.Equal(Localizer.Text("VoiceChangerButtonTip"), ToolTip.GetTip(button));
        Assert.True(button.Bounds.Height > 0 && button.Bounds.Width > 0, $"{where}: the Voice changer button has no size.");
        AssertInside(button, window);
        AssertNoOverlap([.. row.Children.Where(c => c.IsEffectivelyVisible)]);
        if (sounds.IsEffectivelyVisible)
        {
            // After Soundboard on its line, or on a later line where the header wraps.
            Point s = sounds.TranslatePoint(default, row)!.Value, b = button.TranslatePoint(default, row)!.Value;
            Assert.True(b.X >= s.X + sounds.Bounds.Width - 0.5 || b.Y >= s.Y + sounds.Bounds.Height - 0.5,
                $"{where}: the Voice changer button at {b} stands before Soundboard at {s}.");
        }
        return button;
    }

    internal static void Check()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        VoiceChangerWindow? changer = null;
        try
        {
            string? font = Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } named ? named : null;
            if (font is not null) window.FontFamily = new FontFamily(font);
            var main = (MainViewModel)window.DataContext!;
            CaptureInputWindowTests.Apply(main, State(enabled: false));
            window.Show();
            Layout(window, 1040, 900);

            // Dark while every voice is off, lit once one is on.
            Button button = AssertHeaderButton(window, "classic tiles");
            Assert.DoesNotContain("fxOn", button.Classes);
            CaptureInputWindowTests.Apply(main, State(enabled: true));
            Layout(window, 1040, 900);
            Assert.Contains("fxOn", button.Classes);
            Capture(window, "voice-changer-header");

            // The button opens the one window, with no owner; a second press raises it.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            changer = VoiceChangerWindow.Current!;
            Assert.True(changer.IsVisible);
            Assert.Null(changer.Owner);
            Assert.Same(main.VoiceChanger, changer.DataContext);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(changer, VoiceChangerWindow.Current);
            changer.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(VoiceChangerWindow.Current);

            // A window over a recorder: the tiles, a press, and the custom voice's sliders.
            var sent = new Recorder();
            var time = new ManualTime();
            var model = new VoiceChangerViewModel(sent, time);
            model.Apply(State(enabled: false)["mixer"]!["channels"]);
            changer = VoiceChangerWindow.Open(window, model);
            if (font is not null) changer.FontFamily = new FontFamily(font);
            foreach ((double width, double height) in new[] { (660.0, 560.0), (420.0, 380.0) })
            {
                Layout(changer, width, height);
                Button[] tiles = [.. changer.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "VoiceTile")];
                Assert.Equal(12, tiles.Length);
                ComboBox picker = Find<ComboBox>(changer, "VoiceMicrophone");
                Assert.Equal("xlr1", ((VoiceMicrophone)picker.SelectedItem!).Id);
                Button close = Find<Button>(changer, "VoiceClose");
                Assert.True(close.IsCancel);
                foreach (Control control in new Control[] { picker, close, Find<TextBlock>(changer, "VoiceMonitorNote") })
                    AssertInside(control, changer);
                // Every tile is the same width, its name and hint inside it.
                Assert.Single(tiles.Select(t => Math.Round(t.Bounds.Width, 1)).Distinct());
                foreach (Button tile in tiles.Where(t => t.IsEffectivelyVisible))
                    foreach (TextBlock text in tile.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text is { Length: > 0 }))
                        AssertInside(text, tile);
                Capture(changer, $"voice-changer-{width:0}");
            }
            Assert.False(Find<Grid>(changer, "VoiceCustom").IsVisible);
            Button robot = changer.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Name == "VoiceTile" && ((VoiceTileViewModel)b.DataContext!).Voice == "robot");
            robot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(sent.Sent.TryDequeue(out var press));
            Assert.Equal(("xlr1", (bool?)true, "robot"), press);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("fxOn", robot.Classes);

            time.Advance(VoiceChangerViewModel.EchoWait);
            model.Apply(State(enabled: true, voice: "custom")["mixer"]!["channels"]);
            Layout(changer, 660, 560);
            Grid custom = Find<Grid>(changer, "VoiceCustom");
            Assert.True(custom.IsEffectivelyVisible);
            foreach (string name in new[] { "VoicePitch", "VoiceFormant" })
                AssertInside(Find<Slider>(changer, name), changer);
            Assert.Equal(Localizer.Format("VoiceShiftValue", "0"), Find<TextBlock>(changer, "VoicePitchText").Text);
            Capture(changer, "voice-changer-custom");
        }
        finally
        {
            changer?.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The header button and the tiles in several skins' faces.</summary>
    internal static void CheckSkins()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        VoiceChangerWindow? changer = null;
        try
        {
            var main = (MainViewModel)window.DataContext!;
            CaptureInputWindowTests.Apply(main, State(enabled: true));
            window.Show();
            foreach (string skin in new[] { "default", "opendeck", "broadcast", "wave" })
            {
                Assert.Empty(SkinService.Apply(skin == "default" ? new SkinEntry(SkinPackage.Default, [])
                    : Assert.Single(SkinCatalog.Discover(), e => e.Id == skin)));
                Layout(window, 1280, 1200);
                Button button = AssertHeaderButton(window, skin);
                Assert.True(button.Classes.Contains("fxOn"), $"{skin}: the Voice changer button does not light while a voice is on.");
                changer = VoiceChangerWindow.Open(window, main.VoiceChanger);
                Layout(changer, 660, 560);
                Button robot = changer.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.Name == "VoiceTile" && ((VoiceTileViewModel)b.DataContext!).Voice == "robot");
                Assert.Contains("fxOn", robot.Classes);
                foreach (TextBlock text in changer.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text is { Length: > 0 }))
                    Assert.True(text.Foreground is not null, $"{skin}: \"{text.Text}\" has no colour.");
                Capture(changer, $"voice-changer-{skin}");
                changer.Close();
                Dispatcher.UIThread.RunJobs();
                changer = null;
            }
        }
        finally
        {
            changer?.Close();
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
