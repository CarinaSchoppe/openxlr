using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MiniViewTests
{
    [Fact]
    public async Task MiniViewSelectsOneChannelAndMixWithoutChangingRoutingOrLosingFallbackPreferences()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var main = new MainViewModel(client);
            var a = new MixViewModel(client, "monitor", "Monitor A");
            var b = new MixViewModel(client, "monitorb", "Monitor B");
            var chat = new MixViewModel(client, "chat", "Chat") { Kind = "virtualMic" };
            main.Mixes.Add(a); main.Mixes.Add(b); main.Mixes.Add(chat);
            var first = new ChannelViewModel(client, "first", "First", ["monitor", "monitorb", "chat"]);
            var hidden = new ChannelViewModel(client, "hidden", "Hidden", ["monitor", "monitorb", "chat"]);
            hidden.Appearance.Apply(JsonNode.Parse("""{"hidden":true}"""));
            main.Channels.Add(first); main.Channels.Add(hidden);
            foreach (var send in hidden.Sends) send.ApplyFromDaemon(0.37, true);
            main.SelectedCompactChannel = hidden;
            main.SelectedCompactMix = chat;
            main.MiniView = true;
            var mic = new ChannelViewModel(client, "xlr1", "Mic", ["monitor", "monitorb", "chat"]);
            main.Channels.Add(mic); Refresh(main);
            Assert.True(mic.ShowStripInserts);
            Assert.False(first.DisplayVisible); Assert.True(hidden.DisplayVisible);
            Assert.False(a.DisplayVisible); Assert.False(b.DisplayVisible); Assert.True(chat.DisplayVisible);
            Assert.Equal(["chat"], hidden.Sends.Where(s => s.DisplayVisible).Select(s => s.MixId));
            Assert.All(hidden.Sends, send => Assert.Equal(0.37, send.Level));
            Assert.All(hidden.Sends, send => Assert.True(send.Muted));
            Assert.False(main.ShowDetailedSections);
            main.Mixes.Remove(chat); Refresh(main);
            Assert.Same(a, main.SelectedCompactMix);
            Assert.Equal("chat", UiSettings.Load().CompactMix);
            main.Mixes.Add(chat); Refresh(main);
            Assert.Same(chat, main.SelectedCompactMix);
            main.MiniView = false;
            Assert.False(mic.ShowStripInserts);
            Assert.True(first.DisplayVisible); Assert.False(hidden.DisplayVisible);
            Assert.All(main.Mixes, mix => Assert.True(mix.DisplayVisible));
            main.CompactMixes = true;
            Assert.False(a.DisplayVisible); Assert.True(chat.DisplayVisible);
            main.SelectedCompactMix = b;
            Assert.True(b.DisplayVisible); Assert.False(chat.DisplayVisible);
            Assert.Equal("monitorb", UiSettings.Load().CompactMix);
        });
    }

    [Fact]
    public async Task FailedDensityEditsPreserveThePreferenceFileAndDisplayedChoices()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var main = new MainViewModel(client);
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            OpenXLR.UI.OpenXlrPaths.WriteAtomic(path, "{\"unknown\":true,");
            main.MiniView = true; main.CompactMixes = true;
            Assert.False(main.MiniView); Assert.False(main.CompactMixes);
            Assert.Equal("{\"unknown\":true,", File.ReadAllText(path));
            Assert.Contains("could not be saved", main.Status);
            File.Delete(path);
            main.MiniView = true; main.CompactMixes = true;
            Assert.True(UiSettings.Load().MiniView); Assert.True(UiSettings.Load().CompactMixes);
        });
    }

    [Fact]
    public void OlderProfilesKeepDensityAndEditorPreferencesWhileNewProfilesRoundTripSelections()
    {
        var local = new UiSettings { MiniView = true, CompactMixes = true, CompactMix = "chat", Language = "de", OpenNativeEditorDirectly = false };
        var older = local.WithPresentation(new WindowPresentation(), "old");
        Assert.True(older.MiniView); Assert.True(older.CompactMixes); Assert.Equal("chat", older.CompactMix);
        var exported = local.ExportPresentation(); exported.Validate();
        var recalled = new UiSettings { Language = "fr" }.WithPresentation(exported, "new");
        Assert.True(recalled.MiniView); Assert.Equal("chat", recalled.CompactMix);
        Assert.Equal("fr", recalled.Language); Assert.True(recalled.OpenNativeEditorDirectly);
        Assert.Throws<JsonException>(() => (exported with { CompactMix = "bad\nname" }).Validate());
        var explicitFull = local.WithPresentation(exported with { MiniView = false, CompactMixes = false, CompactMix = null }, "full");
        Assert.False(explicitFull.MiniView); Assert.False(explicitFull.CompactMixes); Assert.Null(explicitFull.CompactMix);
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public async Task EditorGearHonoursGlobalPreferenceAndLiveAvailability(bool direct, bool running, bool blocked, bool expected)
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var chain = new InsertsViewModel(client, "xlr1", 1, "Mic");
        chain.PluginChoices.Add(new PluginChoice("test", "Test", "", new JsonArray(), NativeEditorAvailable: true, NativeEditorSupported: true));
        chain.Apply(JsonNode.Parse("""[{"insert":{"id":"first","kind":"lv2","plugin":"test","nativeHost":true},"nativeHostRunning":true}]"""));
        var insert = Assert.Single(chain.Items);
        insert.ApplyFromDaemon(JsonNode.Parse("""{"id":"first","kind":"lv2","plugin":"test","nativeHost":true}""")!, null, running, blocked);
        Assert.Equal(expected, InsertWindows.OpensNativeEditor(insert, new UiSettings { OpenNativeEditorDirectly = direct }));
    }

    private static void Refresh(MainViewModel main) => typeof(MainViewModel).GetMethod("RefreshChannelPresentation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
    private static async Task InConfig(Func<Task> action)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-mini-").FullName;
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory); await action(); }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old); Directory.Delete(directory, true); }
    }
}
