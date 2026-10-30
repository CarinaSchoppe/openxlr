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

    /// <summary>
    /// The whole daemon on a private graph, with the real player. One
    /// connection holds a clip and lets it play out with its key still down;
    /// another then holds it and lets go, and its voice ends at once instead
    /// of playing to the clip's end. Then a key comes up while an import on
    /// the same connection is still checking its file, and the release is
    /// taken before the import's reply. Last, the library is exported and
    /// imported back beside itself over that connection.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task AKeyUpEndsItsVoiceAfterAnEndedHoldAndDuringAnImport()
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
            // The real helper, except that a probe waits while the gate file
            // exists, so an import stays in flight for as long as the test needs.
            string gate = Path.Combine(_directory, "probe-gate");
            var pipeWire = (PipeWireAdapter)typeof(MixerService).GetField("_pipeWire", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            pipeWire.PlayerHost = ExecutableScript.Write(Path.Combine(_directory, "gated-host"), $$"""
                if [ "$1" = probe ] && [ -e '{{gate}}' ]; then
                  touch '{{gate}}.waiting'
                  while [ -e '{{gate}}' ]; do sleep 0.05; done
                fi
                exec '{{NativePluginHost.Executable}}' "$@"
                """);
            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(service.Built);
                await using var server = await SocketTestServer.Start(async (socket, _) => await hub.HandleAsync(socket));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                using var terminal = new ClientWebSocket();
                using var deck = new ClientWebSocket();
                await terminal.ConnectAsync(new Uri(server.Url), deadline.Token);
                Assert.Null(await Ask(terminal, new { cmd = "setSoundboard", enabled = true }, deadline.Token));
                string drum = Path.Combine(_directory, "drum-roll.wav");
                WriteConstantWav(drum, .1, seconds: 3);
                Assert.Null(await Ask(terminal, new { cmd = "importClip", path = drum, name = "Drum roll" }, deadline.Token));
                Assert.True(SpinWait.SpinUntil(() => hub.Snapshot().Mixer!.Soundboard.Clips.Single().Status == SoundboardClipStatus.Ready,
                    TimeSpan.FromSeconds(10)), hub.Snapshot().Mixer!.Soundboard.ToString());
                IReadOnlyList<string> Playing() => hub.Snapshot().Mixer!.Soundboard.Playing;

                // The terminal holds the clip and keeps its key down while the
                // clip plays out. It reads what the daemon pushes all along,
                // so its connection stays open and its hold stays recorded.
                Assert.Null(await Ask(terminal, new { cmd = "playClip", clip = "drum-roll", mode = "hold" }, deadline.Token));
                using var terminalDone = new CancellationTokenSource();
                Task terminalReads = ReadUntil(terminal, terminalDone.Token, deadline.Token);
                AssertLevel(.1, "OpenXLR_stream", "held from the terminal");
                Assert.True(SpinWait.SpinUntil(() => Playing().Count == 0, TimeSpan.FromSeconds(6)), "the clip ended by itself");

                // A Deck key holds the clip and comes up: its voice ends now,
                // not when the clip would have ended. It connects only now, so
                // its queue never fills while nothing reads it.
                await deck.ConnectAsync(new Uri(server.Url), deadline.Token);
                Assert.Null(await Ask(deck, new { cmd = "playClip", clip = "drum-roll", mode = "hold" }, deadline.Token));
                Task deckReads = Drain(deck, TimeSpan.FromMilliseconds(500), deadline.Token);
                AssertLevel(.1, "OpenXLR_stream", "held from the Deck");
                await deckReads;
                Assert.Null(await Ask(deck, new { cmd = "releaseClip", clip = "drum-roll" }, deadline.Token));
                Assert.True(SpinWait.SpinUntil(() => Playing().Count == 0, TimeSpan.FromSeconds(1)),
                    "the Deck's key up ended its voice although the terminal's key never came up");
                deckReads = Drain(deck, TimeSpan.FromSeconds(1), deadline.Token);
                AssertLevel(0, "OpenXLR_stream", "released from the Deck");
                await deckReads;

                // The Deck holds the clip again and starts an import whose
                // check is held at the gate. Its key coming up is taken at
                // once, before the import answers.
                Assert.Null(await Ask(deck, new { cmd = "playClip", clip = "drum-roll", mode = "hold" }, deadline.Token));
                deckReads = Drain(deck, TimeSpan.FromMilliseconds(500), deadline.Token);
                AssertLevel(.1, "OpenXLR_stream", "held from the Deck again");
                await deckReads;
                string snare = Path.Combine(_directory, "snare.wav");
                WriteConstantWav(snare, .1, seconds: 1);
                File.WriteAllText(gate, "");
                await SocketTestServer.Send(deck, new { cmd = "importClip", path = snare, name = "Snare", requestId = "import" }, deadline.Token);
                Assert.True(SpinWait.SpinUntil(() => File.Exists(gate + ".waiting"), TimeSpan.FromSeconds(10)), "the import reached its check");
                (string? released, List<string> before) = await AskCollecting(deck, new { cmd = "releaseClip", clip = "drum-roll" }, deadline.Token);
                Assert.Null(released);
                Assert.DoesNotContain("clipImport", before);
                Assert.True(SpinWait.SpinUntil(() => Playing().Count == 0, TimeSpan.FromSeconds(1)), "the key up was taken while the import ran");
                Assert.True(File.Exists(gate), "the import was still at its check");

                // Let the import finish: its reply and its commandResult follow.
                File.Delete(gate);
                JsonNode? imported = null;
                while (true)
                {
                    JsonNode message = await SocketTestServer.Receive(deck, deadline.Token);
                    if (message["type"]?.GetValue<string>() == "clipImport") imported = message;
                    if (message["type"]?.GetValue<string>() == "commandResult" && message["requestId"]?.GetValue<string>() == "import")
                    {
                        Assert.Null(message["error"]?.GetValue<string>());
                        break;
                    }
                }
                Assert.True(imported?["ok"]?.GetValue<bool>(), imported?.ToJsonString());
                Assert.Equal("snare", imported!["clip"]?.GetValue<string>());

                // The library goes out as an archive and comes back in beside
                // itself, each clip checked by the real helper: the archive's
                // ids are in use here, so each clip takes a new one.
                string archive = Path.Combine(_directory, "board.zip");
                (JsonNode? exported, string? exportError) = await AskReply(deck, new { cmd = "exportSoundboard", path = archive }, "soundboardExport", deadline.Token);
                Assert.Null(exportError);
                Assert.Equal(2, exported?["clips"]?.GetValue<int>());
                (JsonNode? merged, string? mergeError) = await AskReply(deck, new { cmd = "importSoundboard", path = archive, merge = true }, "soundboardImport", deadline.Token);
                Assert.Null(mergeError);
                Assert.Equal(["drum-roll-2", "snare-2"], merged!["clips"]!.AsArray().Select(c => c!.GetValue<string>()));
                Assert.Equal("drum-roll-2", merged["renamed"]!["drum-roll"]!.GetValue<string>());
                Assert.True(SpinWait.SpinUntil(() => hub.Snapshot().Mixer!.Soundboard.Clips.Count(c => c.Status == SoundboardClipStatus.Ready) == 4,
                    TimeSpan.FromSeconds(10)), hub.Snapshot().Mixer!.Soundboard.ToString());

                await terminalDone.CancelAsync();
                await terminalReads;
                await terminal.CloseAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
                await deck.CloseAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
            }
            finally
            {
                lifetime.StopApplication();
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); }
    }

    /// <summary>Send one command with a request id; return its error and the types of the messages that came before its answer.</summary>
    private static async Task<(string? Error, List<string> Before)> AskCollecting(WebSocket socket, object command, CancellationToken stop)
    {
        string request = Guid.NewGuid().ToString("N");
        JsonObject body = JsonSerializer.SerializeToNode(command)!.AsObject();
        body["requestId"] = request;
        await SocketTestServer.Send(socket, body, stop);
        var before = new List<string>();
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            string? type = message["type"]?.GetValue<string>();
            if (type == "commandResult" && message["requestId"]?.GetValue<string>() == request)
                return (message["error"]?.GetValue<string>(), before);
            before.Add(type ?? "");
        }
    }

    /// <summary>Send one command with a request id; return the reply of <paramref name="type"/> that came before its answer, and its error.</summary>
    private static async Task<(JsonNode? Reply, string? Error)> AskReply(WebSocket socket, object command, string type, CancellationToken stop)
    {
        string request = Guid.NewGuid().ToString("N");
        JsonObject body = JsonSerializer.SerializeToNode(command)!.AsObject();
        body["requestId"] = request;
        await SocketTestServer.Send(socket, body, stop);
        JsonNode? reply = null;
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            string? kind = message["type"]?.GetValue<string>();
            if (kind == type) reply = message;
            if (kind == "commandResult" && message["requestId"]?.GetValue<string>() == request)
                return (reply, message["error"]?.GetValue<string>());
        }
    }

    /// <summary>Read what the daemon pushes until <paramref name="done"/> is cancelled, as a client does.</summary>
    private static async Task ReadUntil(WebSocket socket, CancellationToken done, CancellationToken stop)
    {
        // A receive is never cancelled, which would abort the socket: meters
        // come fifteen times a second, so the loop looks at the flag soon.
        while (!done.IsCancellationRequested) await SocketTestServer.Receive(socket, stop);
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
