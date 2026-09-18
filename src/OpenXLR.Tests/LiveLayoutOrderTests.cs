using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class LiveLayoutOrderTests
{
    [Fact]
    public async Task StateReordersChannelsMixesAndSendsWithoutReplacingTheirControls()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Apply(model, ["xlr1", "game", "music"], ["monitor", "stream", "chat", "auxout"]);
        var channels = model.Channels.ToDictionary(c => c.Id);
        var mixes = model.Mixes.ToDictionary(m => m.Id);
        var sends = channels["game"].Sends.ToDictionary(s => s.MixId);
        sends["chat"].ApplyFromDaemon(0.42, true);

        Apply(model, ["xlr1", "music", "game"], ["monitor", "chat", "stream", "auxout"]);

        Assert.Equal(["xlr1", "music", "game"], model.Channels.Select(c => c.Id));
        Assert.Equal(["monitor", "chat", "stream", "auxout"], model.Mixes.Select(m => m.Id));
        foreach (var channel in model.Channels)
        {
            Assert.Same(channels[channel.Id], channel);
            Assert.Equal(model.Mixes.Select(m => m.Id), channel.Sends.Select(s => s.MixId));
        }
        foreach (var mix in model.Mixes) Assert.Same(mixes[mix.Id], mix);
        foreach (var send in channels["game"].Sends) Assert.Same(sends[send.MixId], send);
        Assert.Equal(0.42, sends["chat"].Level);
        Assert.True(sends["chat"].Muted);
    }

    [Fact]
    public async Task StateCombinesInsertionDeletionAndReorderAndUnchangedStateDoesNotChurn()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Apply(model, ["game", "music", "browser"], ["monitor", "stream", "chat"]);
        var music = model.Channels[1];
        var chat = model.Mixes[2];
        var chatSend = music.Sends[2];

        string[] channelIds = ["podcast", "music", "game"];
        string[] mixIds = ["chat", "recording", "monitor"];
        Apply(model, channelIds, mixIds);
        Assert.Equal(channelIds, model.Channels.Select(c => c.Id));
        Assert.Equal(mixIds, model.Mixes.Select(m => m.Id));
        Assert.Same(music, model.Channels[1]);
        Assert.Same(chat, model.Mixes[0]);
        Assert.Same(chatSend, music.Sends[0]);
        foreach (var channel in model.Channels)
            Assert.Equal(mixIds, channel.Sends.Select(s => s.MixId));

        int changes = 0;
        NotifyCollectionChangedEventHandler changed = (_, _) => changes++;
        model.Channels.CollectionChanged += changed;
        model.Mixes.CollectionChanged += changed;
        foreach (var channel in model.Channels) channel.Sends.CollectionChanged += changed;
        Apply(model, channelIds, mixIds);
        Assert.Equal(0, changes);

        Apply(model, [], []);
        Assert.Empty(model.Channels);
        Assert.Empty(model.Mixes);
        Apply(model, ["music"], ["monitor"]);
        Assert.Equal("monitor", Assert.Single(Assert.Single(model.Channels).Sends).MixId);
    }

    [Theory]
    [InlineData("a,b,c,d")]
    [InlineData("d,c,b,a")]
    [InlineData("b,c,d,a")]
    [InlineData("a,c,b,d")]
    [InlineData("d,new,b")]
    [InlineData("")]
    public async Task SendOrderTracksMixOrderAndPreservesSurvivingState(string order)
    {
        await using var client = new DaemonClient();
        var channel = new ChannelViewModel(client, "game", "Game", ["a", "b", "c", "d"]);
        var original = channel.Sends.ToDictionary(s => s.MixId);
        foreach (var send in channel.Sends) send.ApplyFromDaemon(0.25, true);
        string[] ids = order.Split(',', StringSplitOptions.RemoveEmptyEntries);
        channel.SyncSends(ids);
        Assert.Equal(ids, channel.Sends.Select(s => s.MixId));
        foreach (var send in channel.Sends)
            if (original.TryGetValue(send.MixId, out var previous))
            {
                Assert.Same(previous, send);
                Assert.Equal(0.25, send.Level);
                Assert.True(send.Muted);
            }
    }

    internal static void Apply(MainViewModel model, string[] channels, string[] mixes)
    {
        static JsonArray Nodes(string[] ids) => new(ids.Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id, ["name"] = id,
        }).ToArray());
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [new JsonObject { ["channels"] = Nodes(channels), ["mixes"] = Nodes(mixes) }]);
    }
}
