using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>Native Avalonia controls plus real loopback WebSockets; safe for CI without an audio device.</summary>
public sealed class MixerUiTests : IClassFixture<MixerUiSession>
{
    private readonly MixerUiSession _ui;
    public MixerUiTests(MixerUiSession ui) => _ui = ui;

    [Fact]
    public Task RestartAndReleaseNoticeWorkThroughActualControls() => _ui.Run(async () =>
    {
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, State(), stop);
            await Task.Delay(Timeout.Infinite, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var response = new TaskCompletionSource<bool>();
        var service = new ServiceViewModel(() => response.Task);
        var updates = new UpdatesViewModel(_ => Task.FromResult(new UpdateResult(true, "New test build",
            "Development snapshot, not an upstream release.\nRouting and recovery fixes.", "https://github.com/owner/repo")));
        var window = new MainWindow(client, service, updates);
        var main = (MainViewModel)window.DataContext!;
        main.MinimizeToTray = false;
        using var windows = new WindowScope(window);
        window.Show(); // a user's StartMinimized preference must not hide the test owner
        await Until(() => main.DaemonConnected);
        await updates.CheckAsync();
        var restart = window.FindControl<Button>("RestartDaemonButton")!;
        Assert.True(restart.IsVisible && restart.IsEnabled);
        restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(service.Busy);
        Assert.False(restart.IsEnabled);
        response.SetResult(true);
        await Until(() => !service.Busy);
        Assert.True(restart.IsEnabled);
        Assert.Contains("Restart requested", service.Status);
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "View changes"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var changes = Assert.Single(window.OwnedWindows.OfType<UpdatesWindow>());
        Assert.Same(updates, changes.DataContext);
        Assert.Contains("Development snapshot", changes.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text);
        SaveScreenshot(window, "restart-update-header.png");
        SaveScreenshot(changes, "update-notice.png");
        changes.Close();
        using var options = new WindowScope(new OptionsWindow(new OptionsViewModel(client, main)));
        SaveScreenshot(options.Items[0], "options-support.png");
        Assert.NotEmpty(options.Items[0].GetVisualDescendants().OfType<ScrollViewer>());
    });

    [Fact]
    public Task CardsKeepTheirIdentityAndRemoveDeletedSends() => _ui.Run(async () =>
    {
        await using var client = new DaemonClient();
        var main = new MainViewModel(client);
        JsonNode state = State();
        main.Apply(state);
        ChannelViewModel channel = main.Channels[0];
        SendViewModel send = channel.Sends[0];
        using var windows = new WindowScope(new MixerSetupWindow { DataContext = main }, new ChannelEditorWindow(main, channel));
        SaveScreenshot(windows.Items[0], "mixer-layout.png");
        SaveScreenshot(windows.Items[1], "channel-editor.png");
        state["mixer"]!["channels"]![0]!["name"] = "Renamed game";
        state["mixer"]!["mixes"]![0]!["name"] = "Renamed monitor";
        ((JsonArray)state["mixer"]!["mixes"]!).RemoveAt(1);
        main.Apply(state);
        Assert.Same(channel, main.Channels[0]);
        Assert.Same(send, Assert.Single(channel.Sends));
        Assert.Equal("Renamed game", channel.Name);
        Assert.Equal("Renamed game", channel.Inserts.Title);
        Assert.Equal("Renamed monitor", send.MixName);
        state["mixer"] = null;
        main.Apply(state);
        Assert.Empty(main.Channels);
        Assert.False(windows.Items[1].FindControl<ItemsControl>("SendsEditor")!.IsEnabled);
        using var emptyFlow = new WindowScope(new FlowWindow(main));
    });

    [Fact]
    public Task OrderedStateMovesExistingCardsAndTheirSendRows() => _ui.Run(async () =>
    {
        await using var client = new DaemonClient();
        var main = new MainViewModel(client);
        JsonNode state = State();
        ((JsonArray)state["mixer"]!["channels"]!).Add(JsonNode.Parse("""
            {"id":"music","name":"Music","acceptsApps":true,"canDelete":true,
             "levels":{"monitor":0.8,"stream":0.6,"chat":0.4}}
            """));
        ((JsonArray)state["mixer"]!["mixes"]!).Add(JsonNode.Parse("""
            {"id":"chat","name":"Chat","isVirtualMic":true,"canDelete":true}
            """));
        main.Apply(state);
        ChannelViewModel game = main.Channels.Single(c => c.Id == "game");
        MixViewModel stream = main.Mixes.Single(m => m.Id == "stream");

        JsonArray channels = (JsonArray)state["mixer"]!["channels"]!;
        JsonNode musicNode = channels[1]!;
        channels.RemoveAt(1);
        channels.Insert(0, musicNode);
        JsonArray mixes = (JsonArray)state["mixer"]!["mixes"]!;
        JsonNode chatNode = mixes[2]!;
        mixes.RemoveAt(2);
        mixes.Insert(1, chatNode);
        main.Apply(state);

        Assert.Equal(["music", "game"], main.Channels.Select(c => c.Id));
        Assert.Same(game, main.Channels[1]);
        Assert.Equal(["monitor", "chat", "stream"], main.Mixes.Select(m => m.Id));
        Assert.Same(stream, main.Mixes[2]);
        Assert.Equal(["monitor", "chat", "stream"], game.Sends.Select(s => s.MixId));
        Assert.True(main.Channels[0].CanMoveDown);
        Assert.True(main.Channels[1].CanMoveUp);
        Assert.True(main.Mixes[1].CanMoveDown);
        Assert.True(main.Mixes[2].CanMoveUp);
    });

    [Fact]
    public Task ListenButtonSwitchesTheAudibleMixThroughTheWebSocket() => _ui.Run(async () =>
    {
        JsonNode state = State();
        ((JsonArray)state["mixer"]!["mixes"]!).Add(JsonNode.Parse("""
            {"id":"chat","name":"Chat","isVirtualMic":true,"canDelete":true}
            """));
        var commands = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, state, stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "listPlugins")
                {
                    await SocketTestServer.Send(socket,
                        new { type = "plugins", plugins = Array.Empty<object>() }, stop);
                    continue;
                }
                commands.Enqueue(command);
                state["mixer"]!["monitoredMixId"] = command["mix"]!.GetValue<string>();
                await SocketTestServer.Send(socket, state, stop);
                await SocketTestServer.Send(socket, new
                {
                    type = "commandResult",
                    requestId = command["requestId"]!.GetValue<string>(),
                }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var window = new MainWindow(client, new ServiceViewModel(() => Task.FromResult(true)),
            new UpdatesViewModel(_ => Task.FromResult(new UpdateResult(false, "", "", ""))));
        var main = (MainViewModel)window.DataContext!;
        main.MinimizeToTray = false;
        window.Height = 1400;
        using var windows = new WindowScope(window);
        window.Show();
        window.FindControl<Expander>("SubmixerTile")!.IsExpanded = true;
        await Until(() => main.Mixes.Count == 3);
        SaveScreenshot(window, "listen-mix.png");
        await Until(() => window.GetVisualDescendants().OfType<Button>().Any(b =>
            b.DataContext is MixViewModel { Id: "chat" } && Equals(b.Content, "Listen")));
        Button listen = window.GetVisualDescendants().OfType<Button>().Single(b =>
            b.DataContext is MixViewModel { Id: "chat" } && Equals(b.Content, "Listen"));

        listen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await Until(() => main.Mixes.Single(m => m.Id == "chat").IsMonitored);
        JsonNode sent = Assert.Single(commands);
        Assert.Equal("setMonitoredMix", sent["cmd"]!.GetValue<string>());
        Assert.Equal("chat", sent["mix"]!.GetValue<string>());
        Assert.Equal("● Listening", listen.Content);
    });

    [Fact]
    public Task IdenticalInsertIdsInDifferentChannelsDoNotShareWindowsOrSyncKeys() => _ui.Run(async () =>
    {
        await using var client = new DaemonClient();
        var first = new InsertsViewModel(client, "game", 2);
        var second = new InsertsViewModel(client, "music", 2);
        var a = new InsertViewModel(first, "same-id", "lv2", "test:plugin", "First");
        var b = new InsertViewModel(second, "same-id", "lv2", "test:plugin", "Second");
        Assert.NotEqual(first.ParamSyncKey(a.Id, "gain"), second.ParamSyncKey(b.Id, "gain"));
        using var owner = new WindowScope(new Window());
        InsertWindows.OpenControls(owner.Items[0], a);
        InsertWindows.OpenControls(owner.Items[0], b);
        Assert.Equal(2, owner.Items[0].OwnedWindows.Count());
        InsertWindows.RetainChains([]);
        Assert.Empty(owner.Items[0].OwnedWindows);
    });

    [Fact]
    public Task OldDaemonDisablesUnsupportedLayoutInsteadOfSilentlyDroppingEdits() => _ui.Run(async () =>
    {
        await using var client = new DaemonClient();
        var main = new MainViewModel(client);
        JsonNode state = State();
        state["features"] = null;
        main.Apply(state);
        Assert.False(main.CanEditLayout);
        Assert.Contains("matching daemon", main.LayoutNote);
        Assert.False(await main.CreateMix("Not sent"));
    });

    [Fact]
    public Task PluginControlsRenderUnitsAndOverviews() => _ui.Run(async () =>
    {
        await using var client = new DaemonClient();
        var chain = new InsertsViewModel(client, "mix:stream", 2, "Stream mix");
        var metadata = JsonNode.Parse("""
            [{"symbol":"al","name":"Attack threshold","min":0.001,"max":1,"default":0.25,"logarithmic":true,"unit":"gain"},
             {"symbol":"cr","name":"Ratio","min":1,"max":100,"default":4},
             {"symbol":"at","name":"Attack time","min":0,"max":2000,"default":20,"unit":"ms"},
             {"symbol":"rt","name":"Release time","min":0,"max":5000,"default":100,"unit":"ms"}]
            """)!;
        chain.PluginChoices.Add(new PluginChoice("test:compressor", "Compressor", "Dynamics", metadata));
        var insert = new InsertViewModel(chain, "visual", "lv2", "test:compressor", "Compressor");
        insert.EnsureParams();
        using var windows = new WindowScope(new InsertControlsWindow { DataContext = insert });
        Assert.Equal("dB", insert.Params[0].Unit);
        Assert.Equal("ms", insert.Params[2].Unit);
        Assert.Single(windows.Items[0].GetVisualDescendants().OfType<PluginVisualizer>());
        Assert.Equal(4, windows.Items[0].GetVisualDescendants().OfType<ArcKnob>().Count());
        SaveScreenshot(windows.Items[0], "plugin-controls.png");
    });

    [Fact]
    public Task Vst3StateAndSidechainsSurviveUiChainEdits() => _ui.Run(async () =>
    {
        await using var client = new DaemonClient();
        var chain = new InsertsViewModel(client, "xlr1");
        chain.Apply(JsonNode.Parse("""
            [{"insert":{"id":"gain","kind":"vst3","plugin":"001122","label":"Gain",
              "bypass":false,"params":{"1":0.25},"state":"AQID","sidechains":{"aux-1":"channel:chat"}},
              "status":"ready"}]
            """));

        var payload = Assert.IsType<Dictionary<string, object?>>(chain.Items.Single().ToPayload());
        Assert.Equal("vst3", payload["kind"]);
        Assert.Equal("AQID", payload["state"]);
        Assert.Equal("channel:chat",
            Assert.IsType<Dictionary<string, string>>(payload["sidechains"])["aux-1"]);
    });

    [Fact]
    public Task KnobKeyboardKeepsTwoWayBindingAndParameterBounds() => _ui.Run(async () =>
    {
        await using var client = new DaemonClient();
        var insert = new InsertViewModel(new InsertsViewModel(client, "xlr1"), "test", "lv2", "test:eq", "EQ");
        var parameter = new InsertParamViewModel(insert, "gain", "Gain", 0, 10, 5, false, false, false, false, [], "gain");
        var knob = new ArcKnob { Minimum = 0, Maximum = 10, Width = 80, Height = 80 };
        knob.Bind(ArcKnob.ValueProperty, new Binding(nameof(InsertParamViewModel.Value))
        { Source = parameter, Mode = BindingMode.TwoWay });
        using var windows = new WindowScope(new Window { Content = knob, Width = 120, Height = 120 });
        knob.Focus();
        windows.Items[0].KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.True(parameter.Value > 5);
        parameter.ApplyFromDaemon(3);
        Assert.Equal(3, knob.Value); // user input must not replace the binding
        parameter.Value = 100;
        Assert.Equal(10, knob.Value);
        parameter.Value = double.NaN;
        Assert.Equal(10, parameter.Value);
        parameter.Value = 0;
        Assert.Equal("−∞ dB", parameter.ValueText);
    });

    [Fact]
    public Task AddRouteEditDeleteTravelsThroughActualUiAndWebSocket() => _ui.Run(async () =>
    {
        JsonNode state = State();
        ((JsonArray)state["mixer"]!["mixes"]!).Add(JsonNode.Parse("""
            {"id":"chat","name":"Chat","isVirtualMic":true,"canDelete":true}
            """));
        var commands = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, state, stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                commands.Enqueue(command);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "listPlugins")
                {
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = Array.Empty<object>() }, stop);
                    continue;
                }
                if (cmd == "createChannel")
                    ((JsonArray)state["mixer"]!["channels"]!).Add(JsonNode.Parse("""
                        {"id":"qa","name":"QA","acceptsApps":true,"canDelete":true,
                         "levels":{"monitor":0,"stream":0,"chat":0}}
                        """));
                if (cmd is "reorderChannels" or "reorderMixes")
                {
                    JsonArray items = (JsonArray)state["mixer"]![
                        cmd == "reorderChannels" ? "channels" : "mixes"]!;
                    var byId = items.ToDictionary(
                        item => item!["id"]!.GetValue<string>(), item => item!);
                    items.Clear();
                    foreach (JsonNode? orderedId in command["order"]!.AsArray())
                        items.Add(byId[orderedId!.GetValue<string>()]);
                    if (cmd == "reorderMixes")
                    {
                        items.Insert(0, JsonNode.Parse(
                            """{"id":"monitor","name":"Monitor","isMonitor":true}"""));
                    }
                }
                if (cmd == "assignApp") state["mixer"]!["streams"]![0]!["channelId"] = command["channel"]!.GetValue<string>();
                if (cmd == "deleteChannel")
                {
                    JsonArray channels = (JsonArray)state["mixer"]!["channels"]!;
                    JsonNode deleted = channels.Single(c =>
                        c!["id"]!.GetValue<string>() == command["channel"]!.GetValue<string>())!;
                    channels.Remove(deleted);
                }
                await SocketTestServer.Send(socket, state, stop);
                if (command["requestId"] is JsonNode id)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var main = new MainViewModel(client);
        client.Start();
        await Until(() => main.CanEditLayout);
        using var windows = new WindowScope(new MixerSetupWindow { DataContext = main }, new FlowWindow(main));
        var setup = windows.Items[0];
        setup.FindControl<TextBox>("ChannelName")!.Text = "QA";
        setup.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Add channel"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => main.Channels.Count == 2 && !main.LayoutBusy);
        Assert.Equal("Layout saved.", main.LayoutNote);
        Assert.Equal("", setup.FindControl<TextBox>("ChannelName")!.Text);

        await Until(() => setup.GetVisualDescendants().OfType<Button>().Any(b =>
            b.DataContext is ChannelViewModel { Id: "qa" } && Equals(b.Content, "↑")));
        Button channelUp = setup.GetVisualDescendants().OfType<Button>().Single(b =>
            b.DataContext is ChannelViewModel { Id: "qa" } && Equals(b.Content, "↑"));
        channelUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !main.LayoutBusy && main.Channels[0].Id == "qa");
        Assert.Equal("Order saved.", main.LayoutNote);

        await Until(() => setup.GetVisualDescendants().OfType<Button>().Any(b =>
            b.DataContext is MixViewModel { Id: "chat" } && Equals(b.Content, "↑")));
        Button mixUp = setup.GetVisualDescendants().OfType<Button>().Single(b =>
            b.DataContext is MixViewModel { Id: "chat" } && Equals(b.Content, "↑"));
        mixUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !main.LayoutBusy && main.Mixes[1].Id == "chat");

        var flow = windows.Items[1];
        ComboBox picker = flow.GetVisualDescendants().OfType<ComboBox>().Single();
        picker.IsDropDownOpen = true;
        main.Apply(state.DeepClone());
        Assert.Same(picker, flow.GetVisualDescendants().OfType<ComboBox>().Single());
        Assert.True(picker.IsDropDownOpen);
        picker.SelectedItem = main.Apps[0].Channels.Single(c => c.Id == "qa");
        picker.IsDropDownOpen = false;
        await Until(() => commands.Any(c => c["cmd"]?.GetValue<string>() == "assignApp"));
        Assert.Equal("qa", main.Apps[0].ChannelId);

        var channel = main.Channels.Single(c => c.Id == "qa");
        using var editor = new WindowScope(new ChannelEditorWindow(main, channel));
        Slider slider = editor.Items[0].GetVisualDescendants().OfType<Slider>().First();
        slider.SetCurrentValue(Slider.ValueProperty, 0.37);
        await Until(() => commands.Any(c => c["cmd"]?.GetValue<string>() == "setLevel"));
        JsonNode level = commands.Last(c => c["cmd"]?.GetValue<string>() == "setLevel");
        Assert.Equal("qa", level["channel"]!.GetValue<string>());
        Assert.Equal(0.37, level["value"]!.GetValue<double>());
        Assert.True(await main.DeleteChannel("qa"));
        await Until(() => main.Channels.Count == 1);
        Assert.False(editor.Items[0].FindControl<ItemsControl>("SendsEditor")!.IsEnabled);
    });

    [Fact]
    public Task ChannelCardOpensItsOwnNativeEditorAndShowsServerErrors() => _ui.Run(async () =>
    {
        JsonNode state = State();
        state["features"] = new JsonArray("editableLayout", "commandResults", "nativePluginUi", "channelInserts");
        state["mixer"]!["inserts"]!["game"] = JsonNode.Parse("""
            [{"insert":{"id":"compressor","kind":"lv2","plugin":"test:compressor","params":{"gain":0.5}}}]
            """);
        var requests = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, state, stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode request = await SocketTestServer.Receive(socket, stop);
                if (request["cmd"]!.GetValue<string>() == "listPlugins")
                {
                    await SocketTestServer.Send(socket, JsonNode.Parse("""
                        {"type":"plugins","plugins":[{"plugin":"test:compressor","name":"Compressor",
                         "audioIns":2,"audioOuts":2,"hasNativeUi":true,
                         "params":[{"symbol":"gain","name":"Output","min":0,"max":1,"default":1}]}]}
                        """)!, stop);
                    continue;
                }
                if (request["cmd"]!.GetValue<string>() == "listPresets")
                {
                    await SocketTestServer.Send(socket, JsonNode.Parse(
                        """{"type":"presets","chains":[],"plugins":[]}""")!, stop);
                    continue;
                }
                requests.Enqueue(request);
                await SocketTestServer.Send(socket, new
                {
                    type = "commandResult",
                    requestId = request["requestId"]!.GetValue<string>(),
                    error = "Test display unavailable",
                }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var main = new MainViewModel(client);
        client.Start();
        await Until(() => main.SupportsChannelInserts && main.Channels.Count == 1);
        ChannelViewModel channel = main.Channels[0];
        using var editor = new WindowScope(new ChannelEditorWindow(main, channel));
        Button inserts = editor.Items[0].FindControl<Button>("InsertsButton")!;
        Assert.True(inserts.IsEnabled);
        inserts.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Window chainWindow = Assert.Single(editor.Items[0].OwnedWindows);
        Assert.Same(channel.Inserts, chainWindow.DataContext);
        InsertViewModel insert = Assert.Single(channel.Inserts.Items);
        using var controls = new WindowScope(new InsertControlsWindow { DataContext = insert });
        await Until(() => insert.CanOpenNativeUi);
        Button native = controls.Items[0].GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "Native plugin UI…"));
        native.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => insert.NativeUiNote == "Test display unavailable" && !insert.OpeningNativeUi);
        JsonNode sent = Assert.Single(requests);
        Assert.Equal("showInsertUi", sent["cmd"]!.GetValue<string>());
        Assert.Equal("game", sent["channel"]!.GetValue<string>());
        Assert.Equal("compressor", sent["insertId"]!.GetValue<string>());
        state["features"] = new JsonArray("editableLayout", "commandResults");
        main.Apply(state);
        Assert.False(native.IsEnabled);
        Assert.False(inserts.IsEnabled);
        chainWindow.Close();
    });

    internal static JsonNode State() => JsonNode.Parse("""
        {"type":"state","features":["editableLayout","commandResults","layoutOrder","monitorMixSelection"],"connected":false,
         "mixer":{"monitoredMixId":"monitor",
                  "mixes":[{"id":"monitor","name":"Monitor","isMonitor":true},
                             {"id":"stream","name":"Stream","isVirtualMic":true}],
                  "channels":[{"id":"game","name":"Game","acceptsApps":true,"canDelete":true,
                               "levels":{"monitor":0.5,"stream":0.7}}],
                  "streams":[{"identity":"test-app","label":"Test Player","channelId":"game","active":true,"running":true}],
                  "inserts":{}}}
        """)!;

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static void SaveScreenshot(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("OPENXLR_SCREENSHOT_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private sealed class WindowScope(params Window[] windows) : IDisposable
    {
        public Window[] Items { get; } = Show(windows);
        private static Window[] Show(Window[] windows) { foreach (Window window in windows) window.Show(); return windows; }
        public void Dispose() { foreach (Window window in Items) window.Close(); }
    }
}

public sealed class MixerUiSession : IDisposable
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(MixerUiSession), AvaloniaTestIsolationLevel.PerAssembly);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont()
        .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = App.DefaultFontFamily })
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public Task Run(Func<Task> action) => _session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);
    public void Dispose() => _session.Dispose();
}
