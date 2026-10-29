using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class SoundboardIntegrationTests
{
    /// <summary>
    /// The whole daemon on a private graph: a clip pressed in hold mode over
    /// a WebSocket belongs to that connection. Closing it releases the clip
    /// unless another open connection holds it too, and a client that
    /// vanishes without a close releases it the same way. HTTP keeps no
    /// connection, so a hold press there is refused.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task AClipHeldByAConnectionThatGoesAwayIsReleased()
    {
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "config"));
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var lifetime = new SocketLifetime();
            var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(service.Built);
                var mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                // Before the board is on, a clip says why it cannot play.
                SoundboardState off = hub.Snapshot().Mixer!.Soundboard;
                Assert.Equal(Mixer.SoundboardOff, off.Error);

                var closed = new SemaphoreSlim(0);
                await using var server = await SocketTestServer.Start(async (socket, _) =>
                {
                    try { await hub.HandleAsync(socket); }
                    finally { closed.Release(); }
                });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var first = new ClientWebSocket();
                await first.ConnectAsync(new Uri(server.Url), deadline.Token);
                Assert.Null(await Ask(first, new { cmd = "setSoundboard", enabled = true }, deadline.Token));
                string wav = Path.Combine(_directory, "held.wav");
                WriteConstantWav(wav, .1, seconds: 2);
                Assert.Null(await Ask(first, new { cmd = "importClip", path = wav, name = "Held" }, deadline.Token));
                Assert.Null(await Ask(first, new { cmd = "updateClip", clip = "held", loop = true }, deadline.Token));
                Assert.True(SpinWait.SpinUntil(() => hub.Snapshot().Mixer!.Soundboard.Clips.Single().Status == SoundboardClipStatus.Ready,
                    TimeSpan.FromSeconds(10)), hub.Snapshot().Mixer!.Soundboard.ToString());
                SoundboardState ready = hub.Snapshot().Mixer!.Soundboard;
                Assert.Null(ready.Error);
                Assert.Equal("sfx", ready.Channel);

                // HTTP has no connection to release the clip from.
                ApiCommandResult http = await hub.ExecuteForApiAsync("""{"cmd":"playClip","clip":"held","mode":"hold"}""");
                Assert.False(http.Ok);
                Assert.Equal(Mixer.SoundboardHoldNeedsConnection, http.Messages.OfType<ErrorMessage>().Single().Message);
                Assert.Empty(hub.Snapshot().Mixer!.Soundboard.Playing);

                Assert.Null(await Ask(first, new { cmd = "playClip", clip = "held", mode = "hold" }, deadline.Token));
                AssertLevel(.1, "OpenXLR_stream", "held by the first connection");
                using var second = new ClientWebSocket();
                await second.ConnectAsync(new Uri(server.Url), deadline.Token);
                Assert.Null(await Ask(second, new { cmd = "playClip", clip = "held", mode = "hold" }, deadline.Token));
                AssertLevel(.2, "OpenXLR_stream", "held by both connections");

                // The second's key comes up: the first still holds the clip,
                // so its voices go on.
                Assert.Null(await Ask(second, new { cmd = "releaseClip", clip = "held" }, deadline.Token));
                Task draining = Drain(second, TimeSpan.FromSeconds(1), deadline.Token);
                AssertLevel(.2, "OpenXLR_stream", "released by one of two holders");
                await draining;

                // The first goes with a proper close: nobody holds the clip now.
                await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
                await closed.WaitAsync(deadline.Token);
                AssertLevel(0, "OpenXLR_stream", "released when its last holder went away");

                // The second holds it again and vanishes without a close.
                Assert.Null(await Ask(second, new { cmd = "playClip", clip = "held", mode = "hold" }, deadline.Token));
                AssertLevel(.1, "OpenXLR_stream", "held again");
                second.Abort();
                await closed.WaitAsync(deadline.Token);
                AssertLevel(0, "OpenXLR_stream", "released when its holder vanished");
                Assert.True(SpinWait.SpinUntil(() => hub.Snapshot().Mixer!.Soundboard.Playing.Count == 0, TimeSpan.FromSeconds(3)));

                // The board off: every clip says off, and the board says why.
                Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setSoundboard","enabled":false}""")).Ok);
                SoundboardState switched = hub.Snapshot().Mixer!.Soundboard;
                Assert.Equal(Mixer.SoundboardOff, switched.Error);
                Assert.Equal(SoundboardClipStatus.Off, switched.Clips.Single().Status);
                _ = mixer;
            }
            finally
            {
                lifetime.StopApplication();
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); }
    }

    /// <summary>Send one command with a request id and return its error, null on success.</summary>
    private static async Task<string?> Ask(WebSocket socket, object command, CancellationToken stop)
    {
        string request = Guid.NewGuid().ToString("N");
        JsonObject body = JsonSerializer.SerializeToNode(command)!.AsObject();
        body["requestId"] = request;
        await SocketTestServer.Send(socket, body, stop);
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            if (message["type"]?.GetValue<string>() == "commandResult" && message["requestId"]?.GetValue<string>() == request)
                return message["error"]?.GetValue<string>();
        }
    }

    /// <summary>Read what the daemon pushes for a while, as a client does, so its queue never fills.</summary>
    private static async Task Drain(WebSocket socket, TimeSpan duration, CancellationToken stop)
    {
        // Never by cancelling a receive: that aborts the socket. Meters come
        // fifteen times a second, so each read returns soon enough.
        long end = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < end) await SocketTestServer.Receive(socket, stop);
    }

    private sealed class SocketLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
}
