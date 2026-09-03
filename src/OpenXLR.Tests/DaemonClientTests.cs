using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DaemonClientTests
{
    [Fact]
    public async Task ReorderAndMonitorCommandsAreAcknowledgedAndTyped()
    {
        var received = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                received.Enqueue(command);
                await SocketTestServer.Send(socket, new
                {
                    type = "commandResult",
                    requestId = command["requestId"]!.GetValue<string>(),
                }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await client.ReorderChannelsAsync(["music", "game"]));
        Assert.Null(await client.ReorderMixesAsync(["chat", "stream"]));
        Assert.Null(await client.SetMonitoredMixAsync("chat"));

        JsonNode[] commands = [.. received];
        Assert.Equal(["reorderChannels", "reorderMixes", "setMonitoredMix"],
            commands.Select(c => c["cmd"]!.GetValue<string>()));
        Assert.Equal(["music", "game"], commands[0]["order"]!.AsArray()
            .Select(n => n!.GetValue<string>()));
        Assert.Equal("chat", commands[2]["mix"]!.GetValue<string>());
    }

    [Fact]
    public async Task ParallelCatalogRequestsShareOneRequestAndLayoutErrorsAreCorrelated()
    {
        int catalogRequests = 0;
        var releaseCatalog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "listPlugins")
                {
                    Interlocked.Increment(ref catalogRequests);
                    await releaseCatalog.Task.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = new[] { new { name = "Test EQ" } } }, stop);
                }
                else
                    await SocketTestServer.Send(socket, new
                    { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error = "protected mix" }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        client.Start(); // idempotent: no competing receive/reconnect loops
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Hold the reply until all callers are pending. An arbitrary sleep
        // tests scheduler speed rather than request coalescing on busy CI hosts.
        var requests = Enumerable.Range(0, 4).Select(_ => client.RequestPluginsAsync(TimeSpan.FromSeconds(10))).ToArray();
        Assert.All(requests, request => Assert.False(request.IsCompleted));
        releaseCatalog.SetResult();
        var catalogs = await Task.WhenAll(requests);
        Assert.All(catalogs, p => Assert.Equal("Test EQ", p![0]!["name"]!.GetValue<string>()));
        Assert.Equal("protected mix", await client.DeleteMixAsync("monitor"));
        // The command acknowledgement is also a server-side ordering barrier:
        // any accidental extra catalog requests have been consumed by now.
        Assert.Equal(1, catalogRequests);
    }

    [Fact]
    public async Task DisconnectReleasesPendingRequestsAndReconnectsWithoutReplayingEdits()
    {
        int connections = 0;
        var commands = new ConcurrentBag<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            int connection = Interlocked.Increment(ref connections);
            if (connection == 1)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                commands.Add(command["cmd"]!.GetValue<string>());
                socket.Abort();
            }
            else
            {
                await SocketTestServer.Send(socket, new { type = "state", connected = false }, stop);
                await Task.Delay(Timeout.Infinite, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) first.TrySetResult(); };
        client.StateReceived += _ => restored.TrySetResult();
        client.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string? result = await client.CreateChannelAsync("Podcast").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Connection lost", result);
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, connections);
        Assert.Single(commands);
    }

    [Fact]
    public async Task DisconnectedEditFailsPromptlyAndClientDisposalIsIdempotent()
    {
        var client = new DaemonClient();
        Assert.Contains("disconnected", await client.CreateMixAsync("Test"));
        await client.DisposeAsync();
        await client.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(client.Start);
    }
}
