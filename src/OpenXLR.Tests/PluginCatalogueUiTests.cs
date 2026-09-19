using System.Text.Json.Nodes;
using Avalonia.Threading;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>Runs inside the existing Xvfb layout test's real UI dispatcher.</summary>
internal static class PluginCatalogueUiTests
{
    internal static void CheckStaleReplies()
    {
        Task check = Verify();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!check.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(check.IsCompleted, "Catalogue UI check hung.");
        check.GetAwaiter().GetResult();
    }

    private static async Task Verify()
    {
        foreach (bool failed in new[] { false, true })
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = await SocketTestServer.Start(async (socket, stop) =>
            {
                int requests = 0;
                while (!stop.IsCancellationRequested)
                {
                    var command = await SocketTestServer.Receive(socket, stop);
                    if (command["cmd"]!.GetValue<string>() == "auth") continue;
                    Assert.Equal("listPlugins", command["cmd"]!.GetValue<string>());
                    bool first = ++requests == 1;
                    if (first) { received.TrySetResult(); await release.Task.WaitAsync(stop); }
                    string id = command["requestId"]!.GetValue<string>();
                    if (!first || !failed)
                        await SocketTestServer.Send(socket, new
                        {
                            type = "plugins", requestId = id,
                            plugins = new[] { new { plugin = first ? "urn:old" : "urn:new", name = "Effect", audioIns = 1, audioOuts = 1, @params = new object[0] } },
                        }, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id, error = first && failed ? "old failure" : null }, stop);
                }
            });
            await using var client = new DaemonClient(server.Url);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
            client.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var view = new InsertsViewModel(client, "xlr1");
            view.ResetForNewConnection();
            var old = view.LoadPluginsAsync();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            view.ResetForNewConnection();
            release.SetResult();
            await old;
            Assert.Empty(view.PluginChoices);
            Assert.Null(view.Note);
            await view.LoadPluginsAsync();
            var current = Assert.Single(view.PluginChoices);
            Assert.Equal("urn:new", current.Uri);
            view.SelectedPlugin = current;
            Assert.True(view.CanAdd);
            view.ResetForNewConnection();
            Assert.False(view.CanAdd);
        }
    }
}
