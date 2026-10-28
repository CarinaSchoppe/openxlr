using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace OpenXLR.Tests;

/// <summary>
/// What the terminal mixer, the UI's client and the Flatpak UI do when the
/// daemon from before the control socket is still running after an upgrade.
/// </summary>
[Collection("xdg-config")]
public sealed class ClientUpgradeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ox-client-upgrade-").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    /// <summary>A loopback port nothing listens on.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task TheTerminalMixerSaysWhatToRunWhenAnOlderDaemonIsRunning()
    {
        var presence = OpenXLR.Tui.DaemonPresence.OlderDaemon;
        await using var link = new OpenXLR.Tui.DaemonLink($"ws://127.0.0.1:{ClosedPort()}/ws", _ => Task.FromResult(presence));
        var noticed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Changed += () => { if (link.Notice is not null) noticed.TrySetResult(); };
        link.Start();
        await WaitFor(() => link.Notice is not null);
        Assert.Equal("restart the daemon", link.Status);
        Assert.Contains("systemctl --user restart openxlr-daemon", link.Notice);
        presence = OpenXLR.Tui.DaemonPresence.NotRunning;
        await WaitFor(() => link.Notice is null);
        Assert.Equal("daemon not running", link.Status);
    }

    [Fact]
    public async Task TheUiClientReportsTheOlderDaemonOnlyAfterAFailedConnect()
    {
        var presence = OpenXLR.UI.DaemonPresence.OlderDaemon;
        await using var client = new OpenXLR.UI.DaemonClient($"ws://127.0.0.1:{ClosedPort()}/ws", _ => Task.FromResult(presence));
        var seen = new List<OpenXLR.UI.DaemonPresence>();
        client.PresenceChanged += value => { lock (seen) seen.Add(value); };
        client.Start();
        await WaitFor(() => client.Presence == OpenXLR.UI.DaemonPresence.OlderDaemon);
        presence = OpenXLR.UI.DaemonPresence.NotRunning;
        await WaitFor(() => client.Presence == OpenXLR.UI.DaemonPresence.NotRunning);
        lock (seen) Assert.Equal([OpenXLR.UI.DaemonPresence.OlderDaemon, OpenXLR.UI.DaemonPresence.NotRunning], seen);
        // A client of an explicit endpoint looks for no daemon of its own.
        await using var plain = new OpenXLR.UI.DaemonClient($"ws://127.0.0.1:{ClosedPort()}/ws");
        plain.Start();
        await Task.Delay(1500);
        Assert.Equal(OpenXLR.UI.DaemonPresence.NotRunning, plain.Presence);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task TheFlatpakRestartOfAnOlderDaemonSendsTheTokenThenRestart(bool accepted, bool unicode)
    {
        string token = new('b', 64);
        string tokenPath = Path.Combine(_directory, "token");
        File.WriteAllText(tokenPath, token + "\n", unicode ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8);
        var received = new List<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            JsonNode auth = await SocketTestServer.Receive(socket, stop);
            lock (received) received.Add(auth.ToJsonString());
            if (!accepted)
            {
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "unauthorized", stop);
                return;
            }
            await SocketTestServer.Send(socket, new { type = "state" }, stop);
            JsonNode restart = await SocketTestServer.Receive(socket, stop);
            lock (received) received.Add(restart.ToJsonString());
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", stop);
        });
        int port = new Uri(server.Url).Port;
        bool sent = await Task.Run(() => OpenXLR.UI.LegacyDaemonRestart.Request(port, tokenPath));
        Assert.Equal(accepted, sent);
        lock (received)
        {
            Assert.Equal("{\"cmd\":\"auth\",\"token\":\"" + token + "\"}", received[0]);
            if (accepted) Assert.Equal("{\"cmd\":\"restartDaemon\"}", received[1]);
        }
        // Without a token there is nothing to send.
        File.Delete(tokenPath);
        Assert.False(OpenXLR.UI.LegacyDaemonRestart.Request(port, tokenPath));
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready());
    }

    [Fact]
    public async Task ALegacyTokenPipeCannotBlockRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(_directory, "token");
        Assert.True(OpenXLR.Core.ProcessRunner.Run("mkfifo", [path]).Ok);
        int port = ClosedPort();
        Task<Exception?> reading = Task.Run<Exception?>(() => Record.Exception(() =>
            Assert.False(OpenXLR.UI.LegacyDaemonRestart.Request(port, path))));
        try { Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally
        {
            if (!reading.IsCompleted)
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                writer.Write("invalid token"u8);
            }
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ALegacyRestartDeadlineIsNotASuccessfulRestart()
    {
        string tokenPath = Path.Combine(_directory, "token");
        File.WriteAllText(tokenPath, new string('b', 64) + "\n");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            Assert.Equal("auth", (await SocketTestServer.Receive(socket, stop))["cmd"]!.GetValue<string>());
            Assert.Equal("restartDaemon", (await SocketTestServer.Receive(socket, stop))["cmd"]!.GetValue<string>());
            received.TrySetResult();
            await Task.Delay(Timeout.Infinite, stop);
        });
        Task<bool> request = Task.Run(() => OpenXLR.UI.LegacyDaemonRestart.Request(new Uri(server.Url).Port, tokenPath));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(7)));
    }
}
