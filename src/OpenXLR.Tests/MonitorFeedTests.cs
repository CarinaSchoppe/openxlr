using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorFeedTests
{
    [Theory]
    [InlineData("stream", true, false)]
    [InlineData("chat", true, false)]
    [InlineData("auxout", true, false)]
    [InlineData("monitor+chat", true, false)]
    [InlineData("monitor2+monitor", true, true)]
    [InlineData("monitor", true, true)]
    [InlineData("chat+chat", false, false)]
    [InlineData("unknown", false, false)]
    [InlineData("", false, false)]
    public void EveryMixCanFeedAnOutputButOnlyMonitorMixesCanUseTheDirectMicPath(string feed, bool valid, bool direct)
    {
        using var mixer = new Mixer();
        Assert.Equal(valid, mixer.IsMonitorFeed(feed));
        Assert.Equal(direct, mixer.IsMonitorOnlyFeed(feed));
    }

    [Fact]
    public void FeedPickerKeepsApiSumsAndRenamedMixesWithoutSendingCommands()
    {
        int changed = 0;
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, _) => changed++);
        output.SyncFeed([new("monitor", "Monitor A"), new("stream", "Stream"), new("chat", "Chat")], "stream+chat");
        Assert.Equal("stream+chat", output.Feed!.Id);
        Assert.Equal("Stream + Chat", output.Feed.Name);
        output.SyncFeed([new("monitor", "Monitor A"), new("stream", "Recording"), new("chat", "Chat")], "stream+chat");
        Assert.Equal("Recording + Chat", output.Feed!.Name);
        Assert.Equal(0, changed);
        output.Feed = output.Feeds[0];
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task ListeningToAuxExposesItsFadersWithoutInventingAUsbPort()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client) { CapOutputRouting = false };
        typeof(MainViewModel).GetProperty(nameof(model.DeviceConnected))!.SetValue(model, true);
        var state = System.Text.Json.Nodes.JsonNode.Parse("""
            {"mixes":[{"id":"auxout","name":"Aux","kind":"auxPort"}],
             "channels":[{"id":"game","name":"Game"}],"monitorFeeds":{"headset":"auxout"}}
            """);
        var apply = typeof(MainViewModel).GetMethod("ApplyMixer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        apply.Invoke(model, [state]);
        Assert.True(model.Mixes[0].Visible);
        Assert.True(model.Channels[0].Sends[0].Visible);
        Assert.False(model.Mixes[0].ShowAuxPortToggle);
        state!["monitorFeeds"] = new System.Text.Json.Nodes.JsonObject();
        apply.Invoke(model, [state]);
        Assert.False(model.Mixes[0].Visible);
        Assert.False(model.Channels[0].Sends[0].Visible);
    }

    [Fact]
    public void AFeedIsOneMixOrSeveralJoinedWithPlus()
    {
        Assert.Equal(["monitor"], MonitorFeed.Parts("monitor"));
        Assert.Equal(["monitor", "monitor2"], MonitorFeed.Parts("monitor+monitor2"));
        Assert.Equal(["monitor2"], MonitorFeed.Parts(" monitor2 + "));
        Assert.Empty(MonitorFeed.Parts(null));
        Assert.Equal("monitor+monitor2", MonitorFeed.Join(["monitor", "monitor2"]));
        Assert.True(MonitorFeed.Includes("monitor+monitor2", "monitor2"));
        Assert.False(MonitorFeed.Includes("monitor", "monitor2"));
    }

    [Fact]
    public void TheSummedOptionReadsMonitorAPlusB()
    {
        Assert.Equal("Monitor A+B", MainViewModel.SummedName(["Monitor A", "Monitor B"]));
        Assert.Equal("Desk+Booth", MainViewModel.SummedName(["Desk", "Booth"]));
    }
}
