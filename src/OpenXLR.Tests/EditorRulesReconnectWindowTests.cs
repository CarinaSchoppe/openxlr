using OpenXLR.UI.Localization;
using System.Net.WebSockets;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Reflection;
using OpenXLR.UI;

namespace OpenXLR.Tests;

internal static class EditorRulesReconnectWindowTests
{
    internal static void Check()
    {
        Task pending = ReconnectRefreshesAnOpenWindow();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "Editor rule reconnect check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task ReconnectRefreshesAnOpenWindow()
    {
        int connections = 0, requests = 0;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebSocket? first = null;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            int connection = Interlocked.Increment(ref connections);
            if (connection == 1) first = socket;
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "auth") continue;
                if (cmd == "getNativeEditorRules")
                {
                    int request = Interlocked.Increment(ref requests);
                    if (request == 3)
                        await SocketTestServer.Send(socket, new { type = "nativeEditorRulesChanged" }, stop);
                    if (request == 5)
                    {
                        held.TrySetResult();
                        await release.Task.WaitAsync(stop);
                    }
                    string name = request switch { 1 => "Before", 2 => "After", 3 => "Stale", 4 => "Current", _ => "Closed" };
                    await SocketTestServer.Send(socket, new { type = "nativeEditorRules", rules = new[]
                    {
                        new { kind = "clap", plugin = "example", name, defaultBlocked = true, blocked = true }
                    } }, stop);
                }
                else if (cmd == "getDiagnostics")
                    await SocketTestServer.Send(socket, new { type = "diagnostics" }, stop);
                else if (cmd == "listPlugins")
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = Array.Empty<object>() }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var window = new NativeEditorRulesWindow(client, "clap", "example");
        try
        {
            window.Show();
            var list = window.FindControl<ListBox>("RuleList")!;
            await WaitFor(() => list.SelectedItem is NativeEditorRuleRow { Name: "Before" } && list.IsEnabled);
            first!.Abort();
            await WaitFor(() => list.SelectedItem is NativeEditorRuleRow { Name: "After" } && list.IsEnabled);
            Assert.Equal(2, connections);
            var refresh = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Localizer.Text("Refresh")));
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => list.SelectedItem is NativeEditorRuleRow { Name: "Current" } && list.IsEnabled);
            Assert.Equal(4, requests); // A notification during a refresh is not lost.

            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await held.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.Close();
            foreach (string eventName in new[] { "ConnectionChanged", "NativeEditorRulesChanged" })
            {
                var handlers = (Delegate?)typeof(DaemonClient).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);
                Assert.DoesNotContain(handlers?.GetInvocationList() ?? [], handler => ReferenceEquals(handler.Target, window));
            }
            release.TrySetResult();
            Assert.NotNull(await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(3)));
            await Task.Delay(20);
            Assert.Equal("Current", ((NativeEditorRuleRow)list.SelectedItem!).Name);
            Assert.Equal(5, requests);
        }
        finally { release.TrySetResult(); window.Close(); }
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready(), "Editor rules did not refresh after reconnect.");
    }
}
