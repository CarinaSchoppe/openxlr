using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Soundboard window against a fake daemon: the Sounds button stands on
/// SFX's tile alone and keeps the tiles level, it, the tray and Options open
/// the one window, a pad plays on Space and a hold pad releases when the key
/// comes up, a pad that cannot play is off, the meters light the ring and the
/// bar, Stop all and the pad's menu send their commands, the edit window
/// sends what changed, a refused import shows the daemon's sentence, a
/// deleted SFX shows its sentence and the button that creates it again, and
/// the whole window fits at its narrowest and at 2400 logical pixels in the
/// font <c>OPENXLR_LAYOUT_FONT</c> names. Run from the window layout test, on
/// its UI thread. <see cref="CheckSkins"/> runs from the skin test: the
/// button in the classic tiles, the input rack and the mix matrix, and the
/// pads in the skins' own faces.
/// </summary>
internal static class SoundboardWindowTests
{
    private sealed class Daemon
    {
        public bool Available = true;
        public bool Enabled = true;
        public bool HasSfx = true;
        public string? Error;
        public readonly HashSet<string> Playing = [];
        public readonly List<JsonObject> Clips =
        [
            Clip("airhorn", "Airhorn", 2.4, 0.8, "overlap", "#FF7A00"),
            Clip("drumroll", "Drum roll with a name long enough to wrap", 6, 1, "hold", null, icon: "music", loop: true),
            Clip("broken", "Broken", 0, 1, "restart", null, error: "The file could not be decoded."),
        ];

        private static JsonObject Clip(string id, string name, double seconds, double gain, string mode, string? colour,
            string icon = "", bool loop = false, string? error = null) => new()
        {
            ["id"] = id, ["name"] = name, ["seconds"] = seconds, ["gain"] = gain, ["mode"] = mode, ["loop"] = loop,
            ["icon"] = icon, ["colour"] = colour, ["loaded"] = error is null, ["error"] = error, ["playing"] = false,
        };

        public JsonObject Board()
        {
            var clips = new JsonArray();
            // As the daemon says it: a reason exactly when the board cannot play, and every clip off then.
            string? error = !Available ? SoundboardViewModelTests.NeedsHost : !HasSfx ? Error ?? SoundboardViewModelTests.Deleted
                : !Enabled ? SoundboardViewModelTests.Off : null;
            foreach (JsonObject clip in Clips)
            {
                var copy = (JsonObject)clip.DeepClone();
                copy["playing"] = Playing.Contains(clip["id"]!.GetValue<string>());
                bool held = clip["error"] is null;
                copy["status"] = error is not null ? "off" : held ? "ready" : "error";
                copy["loaded"] = error is null && held;
                clips.Add(copy);
            }
            return new JsonObject
            {
                ["channel"] = HasSfx ? "sfx" : null, ["enabled"] = Enabled, ["available"] = Available, ["error"] = error,
                ["formats"] = Available ? new JsonArray("wav", "flac", "ogg", "opus", "mp3") : new JsonArray(),
                ["clips"] = clips, ["playing"] = new JsonArray([.. Playing.Select(p => (JsonNode)p)]),
            };
        }

        public JsonNode State()
        {
            JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(""))!;
            state["type"] = "state";
            state["mixer"] = Mixer(HasSfx, Board());
            return state;
        }

        /// <summary>Apply a command as the daemon would; the error it answers with, or null.</summary>
        public string? Handle(JsonNode cmd)
        {
            string? clip = cmd["clip"]?.GetValue<string>();
            JsonObject? entry = Clips.FirstOrDefault(c => c["id"]!.GetValue<string>() == clip);
            switch (cmd["cmd"]!.GetValue<string>())
            {
                case "setSoundboard":
                    Enabled = cmd["enabled"]!.GetValue<bool>();
                    if (Enabled) { HasSfx = true; Error = null; }
                    return null;
                case "playClip":
                    if (!Enabled || !HasSfx) return SoundboardViewModelTests.Off;
                    if (entry!["mode"]!.GetValue<string>() == "toggle" && !Playing.Add(clip!)) Playing.Remove(clip!);
                    else Playing.Add(clip!);
                    return null;
                case "releaseClip":
                case "stopClip":
                    Playing.Remove(clip!);
                    return null;
                case "stopClips":
                    Playing.Clear();
                    return null;
                case "updateClip":
                    foreach (string field in new[] { "name", "gain", "mode", "loop", "icon", "colour" })
                        if (cmd[field] is { } value) entry![field] = field == "colour" && value.GetValue<string>() == "" ? null : value.DeepClone();
                    return null;
                case "setClipOrder":
                    List<string> order = [.. cmd["clips"]!.AsArray().Select(c => c!.GetValue<string>())];
                    Clips.Sort((a, b) => order.IndexOf(a["id"]!.GetValue<string>()).CompareTo(order.IndexOf(b["id"]!.GetValue<string>())));
                    return null;
                case "deleteClip":
                    Clips.Remove(entry!);
                    Playing.Remove(clip!);
                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary>A mixer with three mixes, the hardware inputs, three application channels with SFX among them, and the board.</summary>
    internal static JsonObject Mixer(bool hasSfx, JsonObject board)
    {
        var channels = new JsonArray();
        void Add(string id, string name, bool hardware = false, string icon = "")
            => channels.Add(new JsonObject
            {
                ["id"] = id, ["name"] = name, ["hardware"] = hardware, ["present"] = true, ["volume"] = id == "sfx" ? 0.7 : 1.0,
                ["levels"] = new JsonObject { ["monitor"] = 1, ["stream"] = 1, ["chat"] = 0.5 },
                ["appearance"] = new JsonObject { ["icon"] = "", ["colour"] = null, ["hidden"] = false, ["defaultIcon"] = icon },
            });
        Add("xlr1", "XLR 1", hardware: true, icon: "mic");
        Add("game", "Game", icon: "game");
        Add("music", "Music", icon: "music");
        if (hasSfx) Add("sfx", "SFX", icon: "burst");
        Add("voicechat", "Voice Chat", icon: "chat");
        return new JsonObject
        {
            ["mixes"] = new JsonArray(
                new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A", ["kind"] = "monitor", ["volume"] = 1 },
                new JsonObject { ["id"] = "stream", ["name"] = "Stream", ["kind"] = "virtualMic", ["volume"] = 1 },
                new JsonObject { ["id"] = "chat", ["name"] = "Chat", ["kind"] = "virtualMic", ["volume"] = 1 }),
            ["channels"] = channels,
            ["inserts"] = new JsonObject(),
            ["soundboard"] = board,
        };
    }

    internal static void Check()
    {
        Task pending = CheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The soundboard window check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckAll()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var daemon = new Daemon();
        FakeSocket? live = null;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var holder = new FakeSocket(socket, stop);
            await holder.Push(daemon.State());
            live = holder;
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                string name = cmd["cmd"]?.GetValue<string>() ?? "";
                if (name is "listPlugins" or "getPluginSetup") continue;
                string? id = cmd["requestId"]?.GetValue<string>();
                if (name == "importClip")
                {
                    commands.Enqueue(cmd);
                    bool ok = cmd["path"]!.GetValue<string>().EndsWith(".wav", StringComparison.Ordinal);
                    string message = ok ? "Applause was added to the soundboard." : "The clip is 31.0 s long; the limit is 30 s.";
                    if (ok) daemon.Clips.Add(new JsonObject
                    {
                        ["id"] = "applause", ["name"] = "Applause", ["seconds"] = 3.0, ["gain"] = 1.0, ["mode"] = "overlap", ["loop"] = false,
                        ["icon"] = "", ["colour"] = null, ["loaded"] = true, ["error"] = null, ["playing"] = false,
                    });
                    await holder.Push(daemon.State());
                    await holder.Push(new JsonObject { ["type"] = "clipImport", ["ok"] = ok, ["message"] = message, ["clip"] = ok ? "applause" : null });
                    await holder.Push(new JsonObject { ["type"] = "commandResult", ["requestId"] = id, ["error"] = ok ? null : message });
                    continue;
                }
                if (!name.Contains("Clip", StringComparison.Ordinal) && name != "setSoundboard") continue;
                string? error = daemon.Handle(cmd);
                commands.Enqueue(cmd);
                // As the daemon does: the state first, then the answer to the request.
                await holder.Push(daemon.State());
                if (id is not null) await holder.Push(new JsonObject { ["type"] = "commandResult", ["requestId"] = id, ["error"] = error });
            }
        });

        var window = new MainWindow(new DaemonClient(server.Url));
        if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } font)
            window.FontFamily = new FontFamily(font);
        var main = (MainViewModel)window.DataContext!;
        var client = (DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var options = new OptionsWindow { DataContext = new OptionsViewModel(client, main) };
        SoundboardWindow? board = null;
        try
        {
            window.Show();
            await Until(() => main.Soundboard.IsReported && main.Soundboard.Clips.Count == 3);
            Layout(window, 1040, 900);

            // The classic tiles: SFX's tile alone carries the Sounds button, and the handle row stays as tall in every tile.
            Button[] sounds = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ChannelSounds" && b.IsEffectivelyVisible)];
            Button fromStrip = Assert.Single(sounds);
            Assert.Equal("sfx", ((ChannelViewModel)fromStrip.DataContext!).Id);
            Assert.Equal(Localizer.Text("SoundsButton"), fromStrip.Content);
            AssertInside(fromStrip, window);
            AssertTilesLevel(window, "classic tiles");
            Capture(window, "soundboard-classic-tiles");

            fromStrip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            board = window.OwnedWindows.OfType<SoundboardWindow>().Single();
            Assert.Same(main.Soundboard, board.DataContext);
            // The tray's entry raises the same window rather than opening a second.
            window.OpenSoundboard();
            Assert.Same(board, SoundboardWindow.Current);

            // Options, AUDIO: the button beside Ducking opens it under Options.
            options.Show();
            Layout(options, 980, 900);
            Button fromOptions = options.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SoundboardButton");
            Assert.True(fromOptions.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("Soundboard"), fromOptions.Content);
            AssertInside(fromOptions, options);
            AssertNoOverlap([options.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DuckingButton"), fromOptions]);
            fromOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows.OfType<SoundboardWindow>());
            board = options.OwnedWindows.OfType<SoundboardWindow>().Single();
            if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } boardFont)
                board.FontFamily = new FontFamily(boardFont);
            Layout(board, 760, 620);

            SoundPad Pad(string id) => board.GetVisualDescendants().OfType<SoundPad>().Single(p => ((SoundClipViewModel)p.DataContext!).Id == id);
            Control Part(string id, string name) => board.GetVisualDescendants().OfType<Control>()
                .Single(c => c.Name == name && c.DataContext is SoundClipViewModel clip && clip.Id == id);
            async Task<JsonNode> Sent(Action change)
            {
                int before = commands.Count;
                change();
                await Until(() => commands.Count == before + 1);
                return commands.ToArray()[^1];
            }
            void Key(Control pad, Avalonia.Input.Key key, bool down) => pad.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent, Key = key, Source = pad,
            });

            // The daemon's board, and nothing sent for it.
            Assert.Equal(["airhorn", "drumroll", "broken"], board.GetVisualDescendants().OfType<SoundPad>().Select(p => ((SoundClipViewModel)p.DataContext!).Id));
            Assert.True(Pad("airhorn").IsEnabled);
            Assert.False(Pad("broken").IsEnabled);
            Assert.Equal("The file could not be decoded.", ((TextBlock)Part("broken", "SoundPadDetail")).Text);
            Assert.True(Part("airhorn", "SoundPadColour").IsEffectivelyVisible);
            Assert.False(Part("drumroll", "SoundPadColour").IsEffectivelyVisible);
            Assert.False(Find<StackPanel>(board, "SoundboardNoticeArea").IsEffectivelyVisible);
            Slider master = Find<Slider>(board, "SoundboardVolume");
            Assert.True(master.IsEffectivelyVisible);
            Assert.Equal(0.7, master.Value, 3);
            Assert.Same(main.Channels.Single(c => c.Id == "sfx"), main.Soundboard.Channel);
            Assert.Empty(commands);

            // Space plays a pad in its clip's own mode; holding the key plays once; a hold pad releases when it comes up.
            JsonNode played = await Sent(() => Key(Pad("airhorn"), Avalonia.Input.Key.Space, true));
            Assert.Equal(("playClip", "airhorn"), (played["cmd"]!.GetValue<string>(), played["clip"]!.GetValue<string>()));
            Key(Pad("airhorn"), Avalonia.Input.Key.Space, true);
            Key(Pad("airhorn"), Avalonia.Input.Key.Space, false);
            await Until(() => main.Soundboard.Clips[0].Playing);
            Assert.Single(commands);
            await Sent(() => Key(Pad("drumroll"), Avalonia.Input.Key.Enter, true));
            JsonNode released = await Sent(() => Key(Pad("drumroll"), Avalonia.Input.Key.Enter, false));
            Assert.Equal("releaseClip", released["cmd"]!.GetValue<string>());

            // The meters light the ring and the bar; the strip's Sounds button lights while a clip plays.
            await live!.Push(JsonNode.Parse("""{"type":"meters","levels":{"ch:sfx":[0.4,0.4],"clip:airhorn":[0.5,1]}}""")!);
            await Until(() => Part("airhorn", "SoundPadRing").IsEffectivelyVisible && ((ProgressBar)Part("airhorn", "SoundPadProgress")).Value == 0.5);
            Assert.True(Part("airhorn", "SoundPadProgress").IsEffectivelyVisible);
            Assert.Contains("fxOn", fromStrip.Classes);
            Assert.Contains("fxOn", Find<Button>(board, "SoundboardStopAll").Classes);
            foreach (double width in new[] { 440d, 2400 })
            {
                Layout(board, width, 900);
                AssertFits(board, $"playing at {width}");
                Capture(board, $"soundboard-playing-{(int)width}");
            }
            Layout(board, 760, 620);

            // Stop all, then the pad's menu: stop off while silent, move later sends the whole order.
            Assert.Equal("stopClips", (await Sent(() => Find<Button>(board, "SoundboardStopAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))))["cmd"]!.GetValue<string>());
            await live.Push(JsonNode.Parse("""{"type":"meters","levels":{"ch:sfx":[0,0]}}""")!);
            await Until(() => !main.Soundboard.AnyPlaying && !Part("airhorn", "SoundPadRing").IsEffectivelyVisible);
            MenuFlyout menu = board.PadMenu(main.Soundboard, main.Soundboard.Clips[0]);
            MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
            Assert.Equal(["SoundClipEdit", "SoundClipStop", "SoundClipEarlier", "SoundClipLater", "SoundClipDelete"], items.Select(i => i.Name));
            Assert.False(items[1].IsEnabled);
            Assert.False(items[2].IsEnabled);
            JsonNode order = await Sent(() => items[3].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            Assert.Equal("""["drumroll","airhorn","broken"]""", order["clips"]!.ToJsonString());
            await Until(() => board.GetVisualDescendants().OfType<SoundPad>().Select(p => ((SoundClipViewModel)p.DataContext!).Id).First() == "drumroll");

            // The edit window: the gain and the mode go in one updateClip, and the pad shows the daemon's answer.
            SoundClipViewModel airhorn = main.Soundboard.Clips.Single(c => c.Id == "airhorn");
            Task editing = board.EditAsync(airhorn);
            await Until(() => board.OwnedWindows.OfType<SoundClipWindow>().Any());
            SoundClipWindow edit = board.OwnedWindows.OfType<SoundClipWindow>().Single();
            Layout(edit, 460, 640);
            AssertFitsDialog(edit);
            Capture(edit, "soundboard-edit");
            Find<Slider>(edit, "SoundClipGain").Value = 40;
            Find<ComboBox>(edit, "SoundClipMode").SelectedIndex = 2;
            JsonNode update = await Sent(() => Find<Button>(edit, "SoundClipSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.Equal("""{"gain":0.4,"mode":"toggle"}""", Fields(update));
            await editing.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => airhorn.Mode == "toggle" && airhorn.Detail == Localizer.Format("SoundClipDetail", 2.4, Localizer.Text("SoundClipModeToggle")));

            // A refused import shows the daemon's own sentence; an accepted one adds its pad.
            await main.Soundboard.ImportAsync(["/home/user/long.flac"]);
            TextBlock status = Find<TextBlock>(board, "SoundboardStatus");
            await Until(() => status.IsEffectivelyVisible && status.Classes.Contains("warn"));
            Assert.Equal("The clip is 31.0 s long; the limit is 30 s.", status.Text);
            await main.Soundboard.ImportAsync(["/home/user/applause.wav"]);
            await Until(() => main.Soundboard.Clips.Count == 4 && status.Text == "Applause was added to the soundboard." && !status.Classes.Contains("warn"));

            // SFX deleted: the daemon's sentence, no strip button, and the button that creates SFX again.
            const string deleted = "The soundboard is off because the SFX channel was deleted. Turn it on again to create SFX, muted in every mix.";
            daemon.HasSfx = false;
            daemon.Enabled = false;
            daemon.Error = deleted;
            await live.Push(daemon.State());
            TextBlock notice = Find<TextBlock>(board, "SoundboardNotice");
            Button turnOn = Find<Button>(board, "SoundboardTurnOn");
            await Until(() => notice.IsEffectivelyVisible && notice.Text == deleted && turnOn.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("SoundboardTurnOnCreate"), turnOn.Content);
            Assert.False(Pad("airhorn").IsEnabled);
            Assert.False(Find<StackPanel>(board, "SoundboardMaster").IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "ChannelSounds" && b.IsEffectivelyVisible);
            foreach (double width in new[] { 440d, 2400 })
            {
                Layout(board, width, 900);
                AssertFits(board, $"without SFX at {width}");
                Capture(board, $"soundboard-no-sfx-{(int)width}");
            }
            JsonNode on = await Sent(() => turnOn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.True(on["enabled"]!.GetValue<bool>());
            await Until(() => main.Soundboard.CanPlay && !notice.IsEffectivelyVisible && Pad("airhorn").IsEnabled);

            // No native helper: the sentence, and nothing to add or turn on.
            daemon.Available = false;
            await live.Push(daemon.State());
            await Until(() => notice.IsEffectivelyVisible && notice.Text == SoundboardViewModelTests.NeedsHost);
            Assert.False(Find<Button>(board, "SoundboardAddFiles").IsEnabled);
            Assert.False(turnOn.IsEffectivelyVisible);
            Layout(board, 440, 700);
            AssertFits(board, "without the helper");
        }
        finally
        {
            board?.Close();
            options.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Fields(JsonNode command)
    {
        var copy = command.DeepClone().AsObject();
        foreach (string drop in new[] { "cmd", "clip", "requestId" }) copy.Remove(drop);
        return copy.ToJsonString();
    }

    /// <summary>
    /// The classic tiles stay level: every tile's name line is as tall, the
    /// one with the Sounds button too, so every meter and send row starts at
    /// one height across the panel.
    /// </summary>
    internal static void AssertTilesLevel(Window window, string where)
    {
        Grid[] lines = [.. window.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "ChannelNameLine" && g.IsEffectivelyVisible)];
        Assert.True(lines.Length > 1, $"{where}: fewer than two tiles.");
        double[] heights = [.. lines.Select(g => Math.Round(g.Bounds.Height, 1)).Distinct()];
        Assert.True(heights.Length == 1, $"{where}: the name lines are {string.Join(", ", heights)} tall.");
        double[] meters = [.. lines.Select(g => ((StackPanel)g.Parent!).Children[((StackPanel)g.Parent!).Children.IndexOf(g) + 1])
            .Select(m => Math.Round(m.TranslatePoint(default, window)!.Value.Y, 1)).Distinct()];
        Assert.True(meters.Length == 1, $"{where}: the meters start at {string.Join(", ", meters)}.");
    }

    /// <summary>Every visible control and text sits inside the window's width, and the pads never scroll sideways.</summary>
    private static void AssertFits(SoundboardWindow window, string where)
    {
        foreach (Control control in window.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or CheckBox or Slider or ProgressBar))
        {
            if (control is TextBlock { Text: null or { Length: 0 } }) continue;
            try { AssertInside(control, window); }
            catch (Xunit.Sdk.XunitException ex) { throw new Xunit.Sdk.XunitException($"{where}: {ex.Message}"); }
        }
        ScrollViewer scroll = Find<ScrollViewer>(window, "SoundboardScroll");
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"{where}: the pads scroll sideways.");
        // The overlay scroll bar paints over the scroller's right edge; the pads stay clear of it.
        foreach (Panel cell in window.GetVisualDescendants().OfType<Panel>().Where(p => p.Name == "SoundPadCell"))
            Assert.True(cell.TranslatePoint(new Point(cell.Bounds.Width, 0), scroll)!.Value.X <= scroll.Viewport.Width - 12,
                $"{where}: a pad runs under the scroll bar.");
        // A pad fills its cell, and its text stays inside it.
        foreach (SoundPad pad in window.GetVisualDescendants().OfType<SoundPad>())
            Assert.True(Math.Abs(pad.Bounds.Width - ((Control)pad.Parent!).Bounds.Width) < 0.5
                && Math.Abs(pad.Bounds.Height - ((Control)pad.Parent!).Bounds.Height) < 0.5,
                $"{where}: a pad is {pad.Bounds.Size} in a {((Control)pad.Parent!).Bounds.Size} cell.");
        foreach (SoundPad pad in window.GetVisualDescendants().OfType<SoundPad>())
            foreach (TextBlock text in pad.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 }))
            {
                Point origin = text.TranslatePoint(default, pad)!.Value;
                Assert.True(origin.X >= -0.5 && origin.X + text.Bounds.Width <= pad.Bounds.Width + 0.5
                    && origin.Y + text.Bounds.Height <= pad.Bounds.Height + 0.5, $"{where}: \"{text.Text}\" runs out of its pad.");
            }
    }

    private static void AssertFitsDialog(Window dialog)
    {
        foreach (Control control in dialog.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or CheckBox or Slider or ComboBox or TextBox))
        {
            if (control is TextBlock { Text: null or { Length: 0 } }) continue;
            if (control.GetVisualAncestors().OfType<Popup>().Any()) continue;
            AssertInside(control, dialog);
        }
    }

    // --- under the skins ---

    /// <summary>
    /// The Sounds button in the classic tiles, an input rack and the mix
    /// matrix of Broadcast and Wave, one per view, inside the window and
    /// opening the window; and the pads under the default, OpenDeck and
    /// Broadcast skins, each in its button face with the soundboard tokens.
    /// Run from the skin test, on its UI thread.
    /// </summary>
    internal static void CheckSkins()
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-sound-skins-" + Guid.NewGuid());
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        SoundboardWindow? board = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            string folder = Path.Combine(data, "openxlr", "skins", "sounds-rack");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "skin.json"), """
                {"schema":1,"name":"sounds-rack","tokens":{},"layout":{"version":1,"regions":{"inputs":{"template":"input-rack"},"submixer":{"template":"mix-banks"}}}}
                """);
            var main = (MainViewModel)window.DataContext!;
            var daemon = new Daemon();
            daemon.Playing.Add("airhorn");
            // Through text, as the socket hands it over: numbers then read as any number type.
            JsonNode state = JsonNode.Parse(daemon.State().ToJsonString())!;
            CaptureInputWindowTests.Apply(main, state);
            main.Soundboard.ApplyMeters(JsonNode.Parse("""{"clip:airhorn":[0.6,1]}""")!.AsObject());
            window.Show();
            foreach ((string skin, string button) in new[]
            {
                ("default", "ChannelSounds"), ("sounds-rack", "RackSounds"), ("broadcast", "MatrixSounds"), ("wave", "MatrixSounds"),
            })
            {
                Assert.Empty(SkinService.Apply(skin == "default" ? new SkinEntry(SkinPackage.Default, [])
                    : Assert.Single(SkinCatalog.Discover(), e => e.Id == skin)));
                Layout(window, 1280, 1200);
                Button[] shown = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible
                    && b.Name is "ChannelSounds" or "RackSounds" or "MatrixSounds")];
                Button sounds = Assert.Single(shown);
                Assert.True(sounds.Name == button, $"{skin}: the Sounds button is {sounds.Name}, not {button}.");
                Assert.True(sounds.Classes.Contains("fxOn"), $"{skin}: the Sounds button does not light while a clip plays.");
                Assert.Equal(Localizer.Format("SoundsButtonName", "SFX"), Avalonia.Automation.AutomationProperties.GetName(sounds));
                AssertInside(sounds, window);
                Assert.True(sounds.Bounds.Height > 0 && sounds.Bounds.Width > 0, $"{skin}: the Sounds button has no size.");
                if (button == "ChannelSounds") AssertTilesLevel(window, skin);
                Shot(window, $"soundboard-strip-{skin}");
                sounds.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                board = SoundboardWindow.Current;
                Assert.NotNull(board);
                Assert.Same(main.Soundboard, board!.DataContext);
                board.Close();
                Dispatcher.UIThread.RunJobs();
            }

            // The pads in each skin's button face, sized and ringed by the soundboard tokens.
            foreach (string skin in new[] { "default", "opendeck", "broadcast" })
            {
                Assert.Empty(SkinService.Apply(skin == "default" ? new SkinEntry(SkinPackage.Default, [])
                    : Assert.Single(SkinCatalog.Discover(), e => e.Id == skin)));
                board = SoundboardWindow.Open(window, main.Soundboard);
                Layout(board, 760, 620);
                SoundPad pad = board.GetVisualDescendants().OfType<SoundPad>().First(p => ((SoundClipViewModel)p.DataContext!).Id == "airhorn");
                Panel cell = (Panel)pad.Parent!;
                Assert.Equal((double)Application.Current!.FindResource("Ox.Soundboard.PadWidth")!, cell.Bounds.Width, 1);
                Assert.Equal((double)Application.Current!.FindResource("Ox.Soundboard.PadHeight")!, cell.Bounds.Height, 1);
                Assert.Same(Application.Current!.FindResource("Ox.Button.Theme") as Avalonia.Styling.ControlTheme, pad.Theme);
                Border ring = cell.Children.OfType<Border>().Single(b => b.Name == "SoundPadRing");
                Assert.True(ring.IsEffectivelyVisible);
                Assert.Same(Application.Current!.FindResource("Ox.Soundboard.Playing"), ring.BorderBrush);
                foreach (TextBlock text in board.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text is { Length: > 0 }))
                    Assert.True(text.Foreground is not null, $"{skin}: \"{text.Text}\" has no colour.");
                Shot(board, $"soundboard-{skin}");
                board.Close();
                Dispatcher.UIThread.RunJobs();
                board = null;
            }
        }
        finally
        {
            board?.Close();
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A screenshot for looking at by eye, where either window job keeps its pictures.</summary>
    private static void Shot(TopLevel window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("OPENXLR_SKIN_ARTIFACTS") is { Length: > 0 } skins ? skins
            : Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_ARTIFACTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>The fake daemon's socket: one send at a time, from its loop or from the test.</summary>
    private sealed class FakeSocket(System.Net.WebSockets.WebSocket socket, CancellationToken stop)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task Push(JsonNode message)
        {
            await _gate.WaitAsync(stop);
            try { await SocketTestServer.Send(socket, message, stop); }
            finally { _gate.Release(); }
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}
