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
    private const int MaxCommandBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
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

    public WebSocketHub(DeviceManager devices, MixerService mixer, ILogger<WebSocketHub> log,
        IHostApplicationLifetime lifetime)
    {
        _devices = devices;
        _mixer = mixer;
        _log = log;
        _stopping = lifetime.ApplicationStopping;
        // Either half changing pushes the combined state, so clients always see
        // device and mixer consistently in one message.
        _devices.StateChanged += ignored => Broadcast(Snapshot());
        _mixer.Changed += () => Broadcast(Snapshot());
        _mixer.MetersUpdated += () =>
        {
            if (_clients.IsEmpty) return;                    // nobody watching
            IReadOnlyDictionary<string, double[]>? levels = _mixer.Meters();
            if (levels is { Count: > 0 }) Broadcast(new MetersMessage(levels));
        };
    }

    /// <summary>Device state plus mixer state, as one message.</summary>
    private StateMessage Snapshot() =>
        _devices.Snapshot() with
        {
            Mixer = _mixer.Snapshot(),
            Devices = _mixer.Devices(),
            Profiles = ActiveDeviceId() is string devId ? OpenXLR.Core.ProfileStore.List(devId) : [],
            Detected = [.. _devices.Detected().Select(d => new DetectedDevice(d.UsbId, d.Name, d.Active))],
        };

    /// <summary>Serve one client for the life of its socket.</summary>
    public async Task HandleAsync(WebSocket socket)
    {
        var client = new Client(socket, _stopping);
        _clients[client.Id] = client;
        try
        {
            await client.SendAsync(Serialize(Snapshot()));   // initial state
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
        if (cmd is null) return;

        switch (cmd.Cmd)
        {
            case "getState":
                await client.SendAsync(Serialize(Snapshot()));
                break;
            case "getDiagnostics":
                await client.SendAsync(Serialize(new DiagnosticsMessage(_devices.DumpBlocks())));
                break;
            case "listPlugins":
                // The first call may block on lilv's scan; keep it off the socket loop's thread.
                IReadOnlyList<OpenXLR.Core.Mixing.PluginInfo> plugins = await Task.Run(() => OpenXLR.Core.Mixing.Lv2Catalog.Plugins);
                await client.SendAsync(Serialize(new PluginsMessage(plugins)));
                break;
            case "set":
                if (cmd.Control is null) { await client.SendAsync(Serialize(new ErrorMessage("set: missing 'control'"))); break; }
                string? err = _devices.Apply(cmd.Control, cmd.Value);  // broadcasts on success
                if (err is not null) await client.SendAsync(Serialize(new ErrorMessage(err)));
                break;
            case "setLevel":
            case "setChannelMuted":
            case "setMixVolume":
            case "setMixMuted":
            case "assignStream":
            case "assignApp":
            case "forgetApp":
            case "setMonitorOutput":
            case "setMonitorOutputs":
            case "setOutputVolume":
            case "setEnforcedDefaults":
            case "setAuxPortEnabled":
            case "setLowCutHz":
            case "setSoftClipGuard":
            case "setInserts":
            case "setInsertBypass":
            case "setInsertParam":
                string? mixErr = _mixer.Apply(cmd);                     // broadcasts on success
                if (mixErr is not null)
                {
                    await client.SendAsync(Serialize(new ErrorMessage(mixErr)));
                    // Controls are optimistic in both clients. Follow an error
                    // with authoritative state so a rejected ClipGuard/plugin
                    // change snaps back instead of looking enabled forever.
                    await client.SendAsync(Serialize(Snapshot()));
                }
                break;
            case "setActiveDevice":
                if (cmd.Device is null) { await client.SendAsync(Serialize(new ErrorMessage("setActiveDevice: missing 'device'"))); break; }
                string? devSelErr = _devices.SetActiveDevice(cmd.Device);
                if (devSelErr is not null) await client.SendAsync(Serialize(new ErrorMessage(devSelErr)));
                break;
            case "saveProfile":
            case "loadProfile":
            case "deleteProfile":
                string? profErr = HandleProfile(cmd);
                if (profErr is not null) await client.SendAsync(Serialize(new ErrorMessage(profErr)));
                else Broadcast(Snapshot());   // list (and loaded state) changed
                break;
            default:
                await client.SendAsync(Serialize(new ErrorMessage($"unknown cmd '{cmd.Cmd}'")));
                break;
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
                    return null;
                case "loadProfile":
                    OpenXLR.Core.Profile? p = OpenXLR.Core.ProfileStore.Load(devId, name);
                    if (p is null) return $"no profile named '{name}'";
                    // Apply both halves; report the first failure but still
                    // try the other half, so a missing device does not block
                    // the mixer scene (and the other way round).
                    string? devErr = p.Device is null ? null : _devices.ApplyProfile(p.Device);
                    string? mixErr = p.Mixer is null ? null : _mixer.ApplyScene(p.Mixer);
                    return devErr ?? mixErr;
                case "deleteProfile":
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
