using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenXLR.Daemon;

/// <summary>
/// Fans device state out to every connected WebSocket client and routes their
/// "set" commands into the <see cref="DeviceManager"/>. One client's change is
/// broadcast to all, so the UI, the OpenDeck plugin, and any CLI stay in sync.
/// </summary>
public sealed class WebSocketHub
{
    internal const int MaxCommandBytes = 64 * 1024;
    internal const int MaxRequestIdLength = 128;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeviceManager _devices;
    private readonly MixerService _mixer;
    private readonly ILogger<WebSocketHub> _log;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    // Receive loops must observe shutdown: an open socket otherwise keeps
    // Kestrel's graceful stop waiting for the whole host timeout (30 s), long
    // enough for systemd to SIGKILL the daemon before the other services
    // ever get to tear down.
    private readonly CancellationToken _stopping;
    // Device events can originate under the USB lock. Never synchronously
    // acquire the mixer lock there: the mixer may be waiting for that device.
    // Coalesce bursts instead of queueing one expensive snapshot per event.
    private readonly Channel<bool> _stateChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, AllowSynchronousContinuations = false });

    public WebSocketHub(DeviceManager devices, MixerService mixer, ILogger<WebSocketHub> log,
        IHostApplicationLifetime lifetime)
    {
        _devices = devices;
        _mixer = mixer;
        _log = log;
        _stopping = lifetime.ApplicationStopping;
        // Either half changing pushes the combined state, so clients always see
        // device and mixer consistently in one message.
        _devices.StateChanged += ignored => _stateChanges.Writer.TryWrite(true);
        _mixer.Changed += () => _stateChanges.Writer.TryWrite(true);
        _ = BroadcastStatesAsync();
        _mixer.MetersUpdated += () =>
        {
            if (_clients.IsEmpty) return;                    // nobody watching
            IReadOnlyDictionary<string, double[]>? levels = _mixer.Meters();
            if (levels is { Count: > 0 }) Broadcast(new MetersMessage(levels));
        };
    }

    private async Task BroadcastStatesAsync()
    {
        try
        {
            await foreach (bool ignored in _stateChanges.Reader.ReadAllAsync(_stopping))
            {
                if (_clients.IsEmpty) continue;
                try { Broadcast(Snapshot()); }
                catch (Exception ex) { _log.LogWarning("state broadcast failed: {Message}", ex.Message); }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    /// <summary>Device state plus mixer state, as one message.</summary>
    // Last recalled or saved profile per device id, for the state message.
    private readonly ConcurrentDictionary<string, string> _activeProfile = new();

    public StateMessage Snapshot()
    {
        StateMessage deviceState = _devices.Snapshot();
        string? deviceId = deviceState.Device?.UsbId;
        return deviceState with
        {
            DaemonVersion = OpenXLR.Daemon.DaemonVersion.Current,
            Features = ["editableLayout", "commandResults", "nativePluginUi", "channelInserts",
                "layoutOrder", "monitorMixSelection", "httpApiV1"],
            ActiveProfile = deviceId is not null && _activeProfile.TryGetValue(deviceId, out string? profile)
                ? profile : null,
            Mixer = _mixer.Snapshot(),
            Devices = _mixer.Devices(),
            Profiles = deviceId is not null ? OpenXLR.Core.ProfileStore.List(deviceId) : [],
            Detected = [.. _devices.Detected().Select(d => new DetectedDevice(d.UsbId, d.Name, d.Active))],
        };
    }

    /// <summary>Serve one client for the life of its socket.</summary>
    public async Task HandleAsync(WebSocket socket)
    {
        var client = new Client(socket, _stopping);
        try
        {
            await client.SendAsync(Serialize(Snapshot()));   // initial state
            // Do not expose this client to meter broadcasts before its first
            // state has been queued. Consumers rely on state being first.
            _clients[client.Id] = client;
            await ReceiveLoop(client);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException
                                   or IOException or InvalidOperationException)
        {
            // A client that vanished without a close handshake (killed
            // process, dropped connection), a send that failed or timed out
            // in the pump, or a queue drop: all ordinary disconnects.
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
        }
    }

    private async Task ReceiveLoop(Client client)
    {
        var buf = new byte[8 * 1024];
        while (client.Socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                try
                {
                    res = await client.Socket.ReceiveAsync(buf, _stopping);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await client.Socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "daemon stopping", grace.Token); }
                    catch (Exception) { /* the client may already be gone */ }
                    return;
                }
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return;
                }
                if (res.MessageType != WebSocketMessageType.Text)
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType,
                        "text messages only", CancellationToken.None);
                    return;
                }
                if (ms.Length + res.Count > MaxCommandBytes)
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                        $"command exceeds {MaxCommandBytes} bytes", CancellationToken.None);
                    return;
                }
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            await Dispatch(client, Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    private async Task Dispatch(Client client, string text)
    {
        Command? cmd;
        try { cmd = JsonSerializer.Deserialize<Command>(text, Json); }
        catch (JsonException ex) { await client.SendAsync(Serialize(new ErrorMessage($"bad json: {ex.Message}"))); return; }
        if (cmd is null)
        {
            await client.SendAsync(Serialize(new ErrorMessage("command must be a JSON object")));
            return;
        }
        if (ValidateRequestId(cmd.RequestId) is string requestError)
        {
            await client.SendAsync(Serialize(new ErrorMessage(requestError)));
            return;
        }

        CommandExecution execution = await ExecuteAsync(cmd);
        if (execution.Result is not null)
            await client.SendAsync(Serialize(execution.Result));

        // Correlated callers need a deterministic state-before-ack ordering,
        // and optimistic callers need a rollback after rejection. Successful
        // uncorrelated dial/fader traffic already receives the coalesced
        // broadcast and must not pay for an extra full snapshot per tick.
        StateMessage? authoritative = execution.Mutated &&
            (cmd.RequestId is not null || execution.Error is not null) ? Snapshot() : null;
        if (authoritative is not null)
            await client.SendAsync(Serialize(authoritative));

        if (cmd.RequestId is not null)
            await client.SendAsync(Serialize(new CommandResultMessage(cmd.RequestId, execution.Error)));

        if (execution.Error is not null)
            await client.SendAsync(Serialize(new ErrorMessage(execution.Error)));
    }

    /// <summary>
    /// Execute one validated command independently of its transport. The HTTP
    /// endpoint and WebSocket endpoint intentionally share this path so they
    /// cannot drift into subtly different audio behaviour.
    /// </summary>
    internal async Task<CommandExecution> ExecuteAsync(Command cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Cmd))
            return CommandExecution.Failed("missing 'cmd'");
        if (ValidateRequestId(cmd.RequestId) is string requestError)
            return CommandExecution.Failed(requestError);

        switch (cmd.Cmd)
        {
            case "getState":
                return CommandExecution.Succeeded(Snapshot());
            case "getDiagnostics":
                return CommandExecution.Succeeded(new DiagnosticsMessage(_devices.DumpBlocks()));
            case "listPlugins":
                // The first call may block on lilv's scan; keep it off the socket loop's thread.
                IReadOnlyList<OpenXLR.Core.Mixing.PluginInfo> plugins = await Task.Run(() => OpenXLR.Core.Mixing.Lv2Catalog.Plugins);
                return CommandExecution.Succeeded(new PluginsMessage(plugins));
            case "set":
                if (cmd.Control is null) return CommandExecution.Failed("set: missing 'control'", mutated: true);
                string? err = _devices.Apply(cmd.Control, cmd.Value);  // broadcasts on success
                return CommandExecution.Mutation(err);
            case "setLevel":
            case "setChannelMuted":
            case "setMixVolume":
            case "setMixMuted":
            case "createChannel":
            case "renameChannel":
            case "deleteChannel":
            case "reorderChannels":
            case "createMix":
            case "renameMix":
            case "deleteMix":
            case "reorderMixes":
            case "assignStream":
            case "assignApp":
            case "forgetApp":
            case "setMonitorOutput":
            case "setMonitorOutputs":
            case "setMonitoredMix":
            case "setOutputVolume":
            case "setEnforcedDefaults":
            case "setAuxPortEnabled":
            case "setLowCutHz":
            case "setSoftClipGuard":
            case "setInserts":
            case "showInsertUi":
            case "setInsertBypass":
            case "setInsertParam":
                string? mixErr = _mixer.Apply(cmd);                     // broadcasts on success
                return CommandExecution.Mutation(mixErr);
            case "setActiveDevice":
                if (cmd.Device is null)
                    return CommandExecution.Failed("setActiveDevice: missing 'device'", mutated: true);
                string? devSelErr = _devices.SetActiveDevice(cmd.Device);
                return CommandExecution.Mutation(devSelErr);
            case "saveProfile":
            case "loadProfile":
            case "deleteProfile":
                string? profErr = HandleProfile(cmd);
                if (profErr is null) Broadcast(Snapshot());   // list (and loaded state) changed
                return CommandExecution.Mutation(profErr);
            default:
                return CommandExecution.Failed($"unknown cmd '{cmd.Cmd}'");
        }
    }

    /// <summary>The active device's usb id, or null while disconnected.</summary>
    private string? ActiveDeviceId() => _devices.Snapshot().Device?.UsbId;

    /// <summary>Save, load, or delete a named profile (per device). Null on success.</summary>
    private string? HandleProfile(Command cmd)
    {
        string? name = OpenXLR.Core.ProfileStore.SanitizeName(cmd.Name);
        if (name is null) return $"{cmd.Cmd}: missing or invalid 'name'";
        if (ActiveDeviceId() is not string devId) return $"{cmd.Cmd}: no device connected";
        try
        {
            switch (cmd.Cmd)
            {
                case "saveProfile":
                    OpenXLR.Core.ProfileStore.Save(devId, name, new OpenXLR.Core.Profile
                    {
                        Device = _devices.Snapshot().State,
                        Mixer = _mixer.ExportScene(),
                    });
                    _activeProfile[devId] = name;
                    return null;
                case "loadProfile":
                    OpenXLR.Core.Profile? p = OpenXLR.Core.ProfileStore.Load(devId, name);
                    if (p is null) return $"no profile named '{name}'";
                    // Apply both halves; report the first failure but still
                    // try the other half, so a missing device does not block
                    // the mixer scene (and the other way round).
                    string? devErr = p.Device is null ? null : _devices.ApplyProfile(p.Device);
                    string? mixErr = p.Mixer is null ? null : _mixer.ApplyScene(p.Mixer);
                    if (devErr is null && mixErr is null) _activeProfile[devId] = name;
                    return devErr ?? mixErr;
                case "deleteProfile":
                    if (_activeProfile.TryGetValue(devId, out string? current) && current == name)
                        _activeProfile.TryRemove(devId, out _);
                    return OpenXLR.Core.ProfileStore.Delete(devId, name) ? null : $"no profile named '{name}'";
                default:
                    return $"unknown profile command '{cmd.Cmd}'";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void Broadcast(object message)
    {
        string text;
        try { text = Serialize(message); }
        catch (Exception ex)
        {
            // A serialization fault here would otherwise vanish: this runs from a
            // timer callback with nobody awaiting it.
            _log.LogError("cannot serialize {type}: {msg}", message.GetType().Name, ex.Message);
            return;
        }
        foreach (Client c in _clients.Values)
        {
            // State is level-triggered and meters are transient, so dropping a
            // frame for a client that cannot keep up is safer than accumulating
            // unbounded tasks and memory. Its next state frame catches it up.
            if (!c.TrySend(text))
                _log.LogDebug("client {id} send queue full; dropping {type}",
                    c.Id, message.GetType().Name);
        }
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json);

    internal static string? ValidateRequestId(string? requestId)
    {
        if (requestId is not null && string.IsNullOrWhiteSpace(requestId))
            return "requestId must not be empty";
        return requestId is { Length: > MaxRequestIdLength }
            ? $"requestId exceeds {MaxRequestIdLength} characters"
            : null;
    }

    internal sealed record CommandExecution(object? Result, string? Error, bool Mutated)
    {
        public static CommandExecution Succeeded(object? result = null) => new(result, null, false);
        public static CommandExecution Failed(string error, bool mutated = false) => new(null, error, mutated);
        public static CommandExecution Mutation(string? error) => new(null, error, true);
    }

    /// <summary>
    /// A connection with one bounded send pump. WebSocket forbids concurrent
    /// sends; the bounded channel also prevents a slow or suspended local
    /// client from growing the daemon's memory indefinitely.
    /// </summary>
    private sealed class Client : IDisposable
    {
        private const int QueueCapacity = 32;
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        private readonly Channel<PendingSend> _outgoing;
        private readonly CancellationTokenSource _lifetime;
        private readonly Task _sendPump;

        public Client(WebSocket socket, CancellationToken stopping)
        {
            Socket = socket;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _outgoing = Channel.CreateBounded<PendingSend>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _sendPump = Task.Run(SendPumpAsync);
        }

        public bool TrySend(string text)
            => Socket.State == WebSocketState.Open &&
               _outgoing.Writer.TryWrite(new PendingSend(text, null));

        public Task SendAsync(string text)
        {
            if (Socket.State != WebSocketState.Open) return Task.CompletedTask;
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_outgoing.Writer.TryWrite(new PendingSend(text, sent))) return sent.Task;
            // The queue holds about two seconds of meter frames. A client that
            // has not drained it is stuck; drop it rather than let the send
            // fault propagate through the command pipeline. The receive loop
            // ends on the aborted socket and the handler cleans up.
            try { Socket.Abort(); } catch (ObjectDisposedException) { }
            return Task.CompletedTask;
        }

        private async Task SendPumpAsync()
        {
            Exception? failure = null;
            PendingSend? active = null;
            try
            {
                await foreach (PendingSend pending in _outgoing.Reader.ReadAllAsync(_lifetime.Token))
                {
                    active = pending;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(SendTimeout);
                    await Socket.SendAsync(Encoding.UTF8.GetBytes(pending.Text),
                        WebSocketMessageType.Text, true, timeout.Token);
                    pending.Completion?.TrySetResult();
                    active = null;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                active?.Completion?.TrySetException(ex);
                try { Socket.Abort(); } catch (ObjectDisposedException) { }
            }
            finally
            {
                failure ??= new OperationCanceledException("client connection closed");
                // Nothing will drain the queue any more: refuse further writes
                // so a send racing the abort fails fast instead of waiting on
                // a completion that never comes.
                _outgoing.Writer.TryComplete(failure);
                while (_outgoing.Reader.TryRead(out PendingSend? pending))
                    pending.Completion?.TrySetException(failure);
            }
        }

        public void Dispose()
        {
            _outgoing.Writer.TryComplete();
            _lifetime.Cancel();
            try { Socket.Dispose(); } catch (ObjectDisposedException) { }
            _ = _sendPump.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        }

        private sealed record PendingSend(string Text, TaskCompletionSource? Completion);
    }
}
