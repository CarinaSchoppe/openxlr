using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using OpenXLR.UI;

namespace OpenXLR.Tests;

internal static class MixerFaderLifecycleWindowTests
{
    internal static void Check()
    {
        Task check = CheckCommands();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!check.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(check.IsCompleted, "Retired fader check hung.");
        check.GetAwaiter().GetResult();
    }

    private static async Task CheckCommands()
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd is "setLevel" or "setMixVolume" or "setChannelMuted" or "setMixMuted")
                    changes.Enqueue($"{cmd}:{command["channel"]}:{command["mix"]}");
                if (command["requestId"] is { } requestId)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var model = new MainViewModel(client);
        int boosts = 0;
        model.DesktopVolumeBoostRequested += _ => boosts++;
        JsonObject State(bool channel, bool mix) => new()
        {
            ["mixes"] = JsonNode.Parse(mix ? """[{"id":"headset","name":"Headset","volume":0.8},{"id":"monitor","name":"Monitor A"}]"""
                : """[{"id":"monitor","name":"Monitor A"}]"""),
            ["channels"] = JsonNode.Parse(channel ? """[{"id":"music","name":"Music","levels":{"headset":0.6,"monitor":0.7}},{"id":"game","name":"Game"}]"""
                : """[{"id":"game","name":"Game"}]"""),
        };
        foreach (bool removeMix in new[] { false, true })
        {
            apply.Invoke(model, [State(true, true)]);
            MixViewModel master = model.Mixes.Single(m => m.Id == "headset");
            ChannelViewModel channel = model.Channels.Single(c => c.Id == "music");
            SendViewModel send = channel.Sends.Single(s => s.MixId == "headset");
            string removedKey = removeMix ? "mixvol:headset" : "lvl:music:headset";
            changes.Clear();
            try
            {
                master.Volume = .24;
                send.Level = .17;
                // A routine state refresh keeps both objects and their pending edits.
                apply.Invoke(model, [State(true, true)]);
                Assert.Same(master, model.Mixes.Single(m => m.Id == "headset"));
                Assert.Equal(.24, master.Volume);
                Assert.Equal(.17, send.Level);
                apply.Invoke(model, [State(removeMix, !removeMix)]);
                Assert.False(SliderSync.RecentlyTouched(removedKey));
                Assert.False(SliderSync.RecentlyTouched("lvl:music:headset"));
                // Detached bindings cannot enqueue new changes for the reused id either.
                send.Level = .91;
                send.Muted = true;
                if (removeMix)
                {
                    master.Volume = 1.5;
                    master.Muted = true;
                    master.VolumeRange.Boost = true;
                    Assert.Equal(0, boosts);
                }
                apply.Invoke(model, [State(true, true)]);
                Assert.Equal(removeMix ? .8 : .24, model.Mixes.Single(m => m.Id == "headset").Volume);
                Assert.Equal(.6, model.Channels.Single(c => c.Id == "music").Sends.Single(s => s.MixId == "headset").Level);
                SliderSync.FlushPending();
                Assert.Null(await client.SetLayoutOrderAsync([], [])); // barrier after every buffered write
                Assert.Equal(removeMix ? Array.Empty<string>() : ["setMixVolume::headset"], changes.ToArray());
            }
            finally
            {
                SliderSync.Forget("mixvol:headset");
                SliderSync.Forget("lvl:music:headset");
            }
        }
    }
}
