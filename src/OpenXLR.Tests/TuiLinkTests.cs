using System.Net.WebSockets;
using System.Text;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class TuiLinkTests : IDisposable
{
    private readonly string _runtime = Path.Combine(Path.GetTempPath(), "openxlr-tui-link-" + Guid.NewGuid());
    private readonly string? _oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

    public TuiLinkTests()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _runtime);
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(OpenXLR.Core.OpenXlrPaths.TokenPath, "test-token");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _oldRuntime);
        Directory.Delete(_runtime, recursive: true);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"type\":false}")]
    [InlineData("{\"type\":[]}")]
    [InlineData("{\"type\":{}}")]
    public async Task MalformedEnvelopesDoNotStopLaterMessages(string message)
    {
        await using DaemonLink link = new();
        link.Receive(message);
        link.Receive("""{"type":"error","message":"Still receiving"}""");
        Assert.Equal("Still receiving", link.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidFramesReconnectWithoutWaitingForTheMessageEnd(bool binary)
    {
        int connections = 0;
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop); // auth
            await SocketTestServer.Receive(socket, stop); // state request
            if (Interlocked.Increment(ref connections) == 1)
            {
                byte[] frame = new byte[64 * 1024];
                Array.Fill(frame, (byte)' ');
                int frames = binary ? 1 : 129; // More than 8 MiB, with no final frame.
                for (int i = 0; i < frames; i++)
                    await socket.SendAsync(frame, binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                        false, stop);
                await SocketTestServer.Receive(socket, stop);
            }
            else
            {
                await SocketTestServer.Send(socket, new { type = "error", message = "Restored" }, stop);
                await SocketTestServer.Receive(socket, stop);
            }
        });
        await using DaemonLink link = new(server.Url);
        link.Changed += () => { if (link.LastError == "Restored") restored.TrySetResult(); };
        link.Start();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(2, connections);
    }

    [Fact]
    public async Task FragmentedUtf8AndSeparateMessagesKeepTheirContents()
    {
        string label = "Café " + new string('x', 70000);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Receive(socket, stop);
            byte[] json = Encoding.UTF8.GetBytes("{\"type\":\"error\",\"message\":\"" + label + "\"}");
            int split = Array.IndexOf(json, (byte)0xc3) + 1;
            await socket.SendAsync(json.AsMemory(0, split), WebSocketMessageType.Text, false, stop);
            await socket.SendAsync(json.AsMemory(split), WebSocketMessageType.Text, true, stop);
            await SocketTestServer.Send(socket, new { type = "meters", levels = new { } }, stop);
            await SocketTestServer.Receive(socket, stop);
        });
        await using DaemonLink link = new(server.Url);
        int changes = 0;
        link.Changed += () => { if (link.LastError == label && Interlocked.Increment(ref changes) == 2) received.TrySetResult(); };
        link.Start();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(label, link.LastError);
    }
}
