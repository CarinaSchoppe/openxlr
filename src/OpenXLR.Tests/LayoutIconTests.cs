using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using CoreIcons = OpenXLR.Core.Mixing.LayoutIcons;
using CoreDefaults = OpenXLR.Core.Mixing.LayoutDefaultIcons;
using TuiApp = OpenXLR.Tui.App;

namespace OpenXLR.Tests;

/// <summary>
/// The built-in icon set and the icon value a layout stores: the table's own
/// rules, the path grammar a skin is held to, the default by kind the daemon
/// publishes, creation with an icon, and how the window, the terminal mixer
/// and the deck table fall back.
/// </summary>
[Collection("xdg-config")]
public sealed class LayoutIconTests
{
    // Characters both DejaVu Sans Mono and Noto Sans Mono draw, plus the legacy
    // glyphs, which the terminal mixer has drawn since appearance existed.
    private static readonly string[] TextFallbacks = ["●", "◆", "▶", "◉", "■", "▲", "○", "◎", "◇", "▣", "◐", "♪", "♫", "✦", "$"];

    [Fact]
    public void TheBuiltInSetIsNamedBoundedAndDrawable()
    {
        Assert.InRange(CoreIcons.BuiltIn.Count, 20, 64);
        Assert.Equal(CoreIcons.BuiltIn.Count, CoreIcons.BuiltIn.Select(i => i.Name).Distinct().Count());
        foreach (LayoutIconDefinition icon in CoreIcons.BuiltIn)
        {
            Assert.True(CoreIcons.IsName(icon.Name), icon.Name);
            Assert.NotEqual(CoreIcons.None, icon.Name);
            Assert.Contains(icon.Text, TextFallbacks);
            Assert.InRange(icon.Path.Length, 1, 1499);
            Assert.True(CoreIcons.IsValidPath(icon.Path), icon.Name);
        }
        foreach (string id in new[] { "mic", "microphone", "headset", "speaker", "jack", "music", "game", "browser", "chat", "people",
                     "broadcast", "camera", "bell", "star", "globe", "cog", "film", "voice", "podcast", "record", "apps", "burst", "monitor", "dollar" })
            Assert.NotNull(CoreIcons.Find(id));
        Assert.All(CoreIcons.Glyphs, glyph => Assert.False(CoreIcons.IsName(glyph)));
    }

    [Theory]
    [InlineData("M0 0h24v24h-24z", true)]
    [InlineData("m1.5-2.5L3,4 5 6zM1 1a2 2 0 0 1 4 0a2 2 0 1 0 -4 0z", true)]
    [InlineData("M1 1a2 2 0 013 3", true)]
    [InlineData("M.5.5l1e1-1E-1c1 2 3 4 5 6s1 2 3 4q1 2 3 4t1 2", true)]
    [InlineData("", false)]
    [InlineData("L1 1", false)]
    [InlineData("M1", false)]
    [InlineData("M1 1 2", false)]
    [InlineData("F1 M0 0h1", false)]
    [InlineData("M0 0h1<script/>", false)]
    [InlineData("M0 0 url(#x)", false)]
    [InlineData("M0 0e", false)]
    [InlineData("M0 0a1 1 0 2 0 1 1", false)]
    [InlineData("M0 0h1e9999", false)]
    [InlineData("M0 0h--1", false)]
    public void PathDataIsHeldToPlainDrawingCommands(string data, bool valid) =>
        Assert.Equal(valid, CoreIcons.IsValidPath(data));

    [Fact]
    public void PathDataHasALengthBound()
    {
        string path = "M0 0" + string.Concat(Enumerable.Repeat("h1", 3000));
        Assert.False(CoreIcons.IsValidPath(path));
        Assert.True(CoreIcons.IsValidPath(path, path.Length));
    }

    [Fact]
    public void AClientDrawsAGlyphAKnownNameNothingForNoneAndOtherwiseTheDefault()
    {
        Assert.Equal("♫", CoreIcons.Resolve("♫", "apps"));
        Assert.Equal("music", CoreIcons.Resolve("music", "apps"));
        Assert.Equal("apps", CoreIcons.Resolve("", "apps"));
        Assert.Equal("apps", CoreIcons.Resolve("a-skin-icon", "apps"));
        Assert.Equal("a-skin-icon", CoreIcons.Resolve("a-skin-icon", "apps", name => name == "a-skin-icon"));
        Assert.Equal("", CoreIcons.Resolve("none", "apps"));
        Assert.Equal("", CoreIcons.Resolve("", ""));
        Assert.Equal("", CoreIcons.Resolve("", "not-built-in"));
        Assert.Equal("✦", CoreIcons.TextFor("star", "apps"));
        Assert.Equal("■", CoreIcons.TextFor("", "apps"));
        Assert.Equal("♪", CoreIcons.TextFor("♪", "apps"));
        Assert.Equal("", CoreIcons.TextFor("none", "apps"));
    }

    [Fact]
    public void EveryItemCarriesADefaultIconByKind()
    {
        using var mixer = new Mixer();
        MixerState state = mixer.Snapshot();
        Assert.Equal(new Dictionary<string, string>
        {
            ["xlr1"] = "mic", ["xlr2"] = "mic", ["aux"] = "jack", ["line"] = "jack", ["game"] = "game", ["music"] = "music",
            ["browser"] = "browser", ["system"] = "cog", ["voicechat"] = "chat", ["sfx"] = "burst",
        }, state.Channels.ToDictionary(c => c.Id, c => c.Appearance.DefaultIcon));
        Assert.Equal(new Dictionary<string, string>
        {
            ["monitor"] = "headset", ["monitor2"] = "headset", ["stream"] = "broadcast", ["chat"] = "people", ["auxout"] = "jack",
        }, state.Mixes.ToDictionary(m => m.Id, m => m.Appearance.DefaultIcon));
        Assert.Equal("camera", CoreDefaults.ForChannel("desk-camera", false, "alsa_input.usb-cam"));
        Assert.Equal("apps", CoreDefaults.ForChannel("podcast", false, null));
        Assert.Equal("mic", CoreDefaults.ForChannel("xlr3", true, null));
        // A microphone strip: the XLR microphone on a real jack, the handheld one on a capsule
        // or another source, the headset on a Bluetooth headset's source; Aux In stays a jack.
        Assert.Equal("microphone", CoreDefaults.ForChannel("xlr1", true, null, xlrJack: false));
        Assert.Equal("microphone", CoreDefaults.ForChannel("xlr1", true, null, "alsa_input.usb-046d_Logi_4K_Stream_Edition-03.analog-stereo"));
        Assert.Equal("headset", CoreDefaults.ForChannel("xlr2", true, null, "bluez_input.12_34_56_78_9A_BC.1"));
        Assert.Equal("jack", CoreDefaults.ForChannel("aux", true, null, xlrJack: false));
        // A capture input: a headset on a Bluetooth source, a microphone on one mono port.
        Assert.Equal("headset", CoreDefaults.ForChannel("cam", false, "bluez_input.12_34_56_78_9A_BC.1"));
        Assert.Equal("microphone", CoreDefaults.ForChannel("brio", false, "alsa_input.usb-cam", captureMonoChannel: 0));
        Assert.Equal("podcast", CoreDefaults.ForMix("recording", "virtualMic"));
        Assert.Equal("headset", CoreDefaults.ForMix("headphones", "monitor"));
        Assert.All(state.Channels.Select(c => c.Appearance.DefaultIcon).Concat(state.Mixes.Select(m => m.Appearance.DefaultIcon)),
            name => Assert.NotNull(CoreIcons.Find(name)));

        // The state names the field the way docs/api.md does.
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(state, options))!;
        Assert.Equal("music", json["channels"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "music")!["appearance"]!["defaultIcon"]!.GetValue<string>());
        Assert.Equal("", json["mixes"]![0]!["appearance"]!["icon"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("none", true)]
    [InlineData("♫", true)]
    [InlineData("star", true)]
    [InlineData("a-skin-icon", true)]
    [InlineData("<svg/>", false)]
    [InlineData("Star", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg", false)]
    public void CreationTakesTheSameIconValue(string? icon, bool valid)
    {
        using var mixer = new Mixer();
        Assert.Equal(valid, CommandValidation.Check(new Command { Cmd = "createChannel", Name = "Podcast", Icon = icon }, mixer, _ => null) is null);
        Assert.Equal(valid, CommandValidation.Check(new Command { Cmd = "createMix", Name = "Recorder", Kind = "monitor", Icon = icon }, mixer, _ => null) is null);
        var command = JsonSerializer.Deserialize<Command>("""{"cmd":"createMix","name":"Rec","icon":"record"}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal("record", command.Icon);
    }

    [Fact]
    public void ASavedLayoutKeepsANameItDoesNotKnow()
    {
        var saved = new MixerSettings
        {
            Appearance = new()
            {
                ["channel:music"] = new("a-skin-icon"),
                ["channel:game"] = new("none"),
                ["channel:sfx"] = new("Not A Name"),
            },
        };
        MixerSettings sanitized = SavedMixerValidation.Sanitize(saved, out _);
        Assert.Equal(["channel:game", "channel:music"], sanitized.Appearance.Keys.Order());
    }

    [Fact]
    public void TheDeckTableIsTheSameTable()
    {
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,
            "plugin", "com.emaspa.openxlr.sdPlugin", "icons.json")));
        Assert.Equal(CoreIcons.Glyphs, file.RootElement.GetProperty("glyphs").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(CoreIcons.BuiltIn.Select(i => (i.Name, i.Text, i.Path)),
            file.RootElement.GetProperty("icons").EnumerateObject()
                .Select(p => (p.Name, p.Value.GetProperty("text").GetString()!, p.Value.GetProperty("path").GetString()!)));
    }

    [Fact]
    public void TheWindowFallsBackToTheDefaultAndHidesNothingButNone()
    {
        var appearance = new OpenXLR.UI.LayoutAppearanceViewModel();
        appearance.Apply(JsonNode.Parse("""{"icon":"","defaultIcon":"music"}"""));
        Assert.True(appearance.HasIcon);
        Assert.Equal("music", appearance.DefaultIcon);
        appearance.Apply(JsonNode.Parse("""{"icon":"none","defaultIcon":"music"}"""));
        Assert.False(appearance.HasIcon);
        appearance.Apply(JsonNode.Parse("""{"icon":"<svg/>","defaultIcon":"Bad Name"}"""));
        Assert.Equal("", appearance.Icon);
        Assert.Equal("", appearance.DefaultIcon);
        Assert.False(appearance.HasIcon);
        appearance.Apply(JsonNode.Parse("""{"icon":"♪"}"""));
        Assert.True(appearance.HasIcon);
    }

    [Fact]
    public void TheTerminalDrawsANamedIconAsItsCharacter()
    {
        var link = new OpenXLR.Tui.DaemonLink();
        link.Receive("""
            {
              "type": "state", "connected": true,
              "mixer": {
                "mixes": [ { "id": "monitor", "name": "Monitor A", "volume": 1, "kind": "monitor",
                             "appearance": { "icon": "", "defaultIcon": "headset" } } ],
                "channels": [
                  { "id": "music", "name": "Music", "levels": { "monitor": 1 }, "mutedIn": [],
                    "appearance": { "icon": "", "defaultIcon": "music" } },
                  { "id": "game", "name": "Game", "levels": { "monitor": 1 }, "mutedIn": [],
                    "appearance": { "icon": "none", "defaultIcon": "game" } },
                  { "id": "sfx", "name": "SFX", "levels": { "monitor": 1 }, "mutedIn": [],
                    "appearance": { "icon": "a-skin-icon", "defaultIcon": "burst" } }
                ]
              }
            }
            """);
        var app = new TuiApp(link, OpenXLR.Tui.Theme.Material);
        app.ShowTab(0);
        var screen = new OpenXLR.Tui.Screen(140, 36);
        app.Draw(screen);
        string text = string.Join("\n", Enumerable.Range(0, screen.Height)
            .Select(y => new string([.. Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch)])));
        Assert.Contains("♫ Music", text);
        Assert.Contains("✦ SFX", text);
        Assert.Contains("◉ Monitor A", text);
        Assert.Contains("Game", text);
        Assert.DoesNotContain("◆ Game", text);
    }

    [Fact]
    public void TheHeadphoneOutputsTakeAnIconAndAColourButAreNeverHidden()
    {
        using var mixer = new Mixer();
        Command command = new() { Cmd = "setLayoutAppearance", Output = "phones1", Appearance = new("star", "#AA3355") };
        Assert.Null(CommandValidation.Check(command, mixer, _ => null));
        Assert.Null(CommandValidation.Check(command with { Output = "phones2" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Output = "phones3" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Appearance = new("star", null, true) }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = "game" }, mixer, _ => null));
        var wire = JsonSerializer.Deserialize<Command>("""{"cmd":"setLayoutAppearance","output":"phones2","appearance":{"icon":"none"}}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal("phones2", wire.Output);

        Assert.Equal(new Dictionary<string, LayoutAppearanceState>
        {
            ["phones1"] = new("", null, false, "headset"), ["phones2"] = new("", null, false, "headset"),
        }, mixer.Snapshot().OutputAppearance);
        FieldInfo built = typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!;
        built.SetValue(mixer, true);
        try
        {
            MixerSettings? saved = null;
            mixer.SetLayoutAppearance("output:phones1", new("star", "#AA3355"), settings => { saved = settings; return null; });
            Assert.Equal(new LayoutAppearance("star", "#AA3355"), saved!.Appearance["output:phones1"]);
            Assert.Equal(new LayoutAppearanceState("star", "#AA3355", false, "headset"), mixer.Snapshot().OutputAppearance["phones1"]);
            Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("output:phones1", new(Hidden: true), _ => null));
            Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("output:line", new("star"), _ => null));
        }
        finally { built.SetValue(mixer, false); }

        MixerSettings sanitized = SavedMixerValidation.Sanitize(new MixerSettings
        {
            Appearance = new()
            {
                ["output:phones2"] = new("music"),
                ["output:phones3"] = new("music"),
                ["output:phones1"] = new(Hidden: true),
            },
        }, out _);
        Assert.Equal("output:phones2", Assert.Single(sanitized.Appearance).Key);
    }

    private static string Root
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }
}
