using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WebSocketSendQueueTests
{
    [Fact]
    public async Task StoppingABlockedReceiveClosesThePausedSocketWithItsReason()
    {
        using var socket = new PausedSocket();
        using var stop = new CancellationTokenSource();
        Task<(SocketGuard.Outcome Outcome, byte[]? Message)> receive = SocketGuard.ReceiveMessageAsync(
            socket, new byte[64], 1024, SocketGuard.MessageDeadline, stop.Token);
        await socket.Receiving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();

        var result = await receive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SocketGuard.Outcome.Stopping, result.Outcome);
        Assert.Null(result.Message);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.Equal("daemon stopping", socket.CloseStatusDescription);
    }

    [Fact]
    public async Task ChangesDuringTheInitialSendFollowTheInitialState()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-socket-start-").FullName;
        string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        try
        {
            ApiToken.Initialize();
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var lifetime = new Lifetime();
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            using var socket = new PausedSocket
            {
                Authentication = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd = "auth", token = ApiToken.Current })),
            };
            Task serving = hub.HandleAsync(socket);
            try
            {
                await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                hub.Broadcast(new NativeEditorRulesChangedMessage());
                socket.Resume();
                await socket.RulesChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using JsonDocument first = JsonDocument.Parse(socket.Messages.First());
                Assert.Equal("state", first.RootElement.GetProperty("type").GetString());
            }
            finally
            {
                lifetime.StopApplication();
                await serving.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FullQueueCannotSilentlyLoseTheLastState()
    {
        using var socket = new PausedSocket();
        using var client = new WebSocketHub.Client(socket, CancellationToken.None);
        await FillQueue(client, socket);

        Assert.False(client.TrySend(Encoding.UTF8.GetBytes("final state")));
        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    [Fact]
    public async Task MeterOverflowMayDropAndRecoverWithoutLosingLaterState()
    {
        using var socket = new PausedSocket();
        using var client = new WebSocketHub.Client(socket, CancellationToken.None);
        await FillQueue(client, socket);

        Assert.False(client.TrySend([3], transient: true));
        Assert.Equal(WebSocketState.Open, socket.State);
        socket.Resume();
        await socket.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.SendAsync(Encoding.UTF8.GetBytes("final state")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(34, socket.Messages.Count);
        Assert.Equal("final state", Encoding.UTF8.GetString(socket.Messages.Last()));
        Assert.DoesNotContain(socket.Messages, message => message.SequenceEqual(new byte[] { 3 }));
    }

    [Fact]
    public async Task ClosedSocketRefusesBothBroadcastsAndRepliesWithoutWaiting()
    {
        using var socket = new PausedSocket();
        using var client = new WebSocketHub.Client(socket, CancellationToken.None);
        socket.Abort();
        Assert.False(client.TrySend([1]));
        Assert.False(client.TrySend([2], transient: true));
        await client.SendAsync([3]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(socket.Messages);
    }

    [Fact]
    public async Task CommandReplyOverflowDisconnectsAndReleasesPendingSend()
    {
        using var socket = new PausedSocket();
        using var client = new WebSocketHub.Client(socket, CancellationToken.None);
        Task first = client.SendAsync([1]);
        await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 32; i++) Assert.True(client.TrySend([2]));

        await client.SendAsync([3]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Aborted, socket.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task FillQueue(WebSocketHub.Client client, PausedSocket socket)
    {
        Assert.True(client.TrySend([1]));
        await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 32; i++) Assert.True(client.TrySend([2]));
    }

    private sealed class PausedSocket : WebSocket
    {
        private readonly CancellationTokenSource _aborted = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Receiving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RulesChanged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Authentication { get; set; }
        public ConcurrentQueue<byte[]> Messages { get; } = new();
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override string? SubProtocol => null;

        public void Resume() => _release.TrySetResult();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
            Entered.TrySetResult();
            await _release.Task.WaitAsync(stop.Token);
            Messages.Enqueue(buffer.ToArray());
            if (Messages.Count == 33) Drained.TrySetResult();
            if (Encoding.UTF8.GetString(buffer).Contains("nativeEditorRulesChanged", StringComparison.Ordinal))
                RulesChanged.TrySetResult();
        }

        public override void Abort()
        {
            Volatile.Write(ref _state, (int)WebSocketState.Aborted);
            _aborted.Cancel();
        }

        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
            _aborted.Cancel();
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (Authentication is { } auth)
            {
                Authentication = null;
                auth.AsSpan().CopyTo(buffer.AsSpan());
                return new(auth.Length, WebSocketMessageType.Text, true);
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
            Task pending = Task.Delay(Timeout.Infinite, stop.Token);
            Receiving.TrySetResult();
            await pending;
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
}
