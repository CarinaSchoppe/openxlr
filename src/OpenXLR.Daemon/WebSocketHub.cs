using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenXLR.Daemon;

/// <summary>
/// Fans device state out to every connected WebSocket client and routes their
/// "set" commands into the <see cref="DeviceManager"/>. One client's change is
/// broadcast to all, so the UI, the OpenDeck plugin, and any CLI stay in sync.
/// </summary>
public sealed class WebSocketHub
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeviceManager _devices;
    private readonly MixerService _mixer;
    private readonly ILogger<WebSocketHub> _log;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

    public WebSocketHub(DeviceManager devices, MixerService mixer, ILogger<WebSocketHub> log)
    {
        _devices = devices;
        _mixer = mixer;
        _log = log;
        // Either half changing pushes the combined state, so clients always see
        // device and mixer consistently in one message.
        _devices.StateChanged += ignored => { _ = BroadcastAsync(Snapshot()); };
        _mixer.Changed += () => { _ = BroadcastAsync(Snapshot()); };
        _mixer.MetersUpdated += () =>
        {
            if (_clients.IsEmpty) return;                    // nobody watching
            IReadOnlyDictionary<string, double[]>? levels = _mixer.Meters();
            if (levels is { Count: > 0 }) _ = BroadcastAsync(new MetersMessage(levels));
        };
    }

    /// <summary>Device state plus mixer state, as one message.</summary>
    private StateMessage Snapshot() =>
        _devices.Snapshot() with { Mixer = _mixer.Snapshot(), Devices = _mixer.Devices() };

    /// <summary>Serve one client for the life of its socket.</summary>
    public async Task HandleAsync(WebSocket socket)
    {
        var client = new Client(socket);
        _clients[client.Id] = client;
        try
        {
            await client.SendAsync(Serialize(Snapshot()));   // initial state
            await ReceiveLoop(client);
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
                res = await client.Socket.ReceiveAsync(buf, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
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
                string? mixErr = _mixer.Apply(cmd);                     // broadcasts on success
                if (mixErr is not null) await client.SendAsync(Serialize(new ErrorMessage(mixErr)));
                break;
            default:
                await client.SendAsync(Serialize(new ErrorMessage($"unknown cmd '{cmd.Cmd}'")));
                break;
        }
    }

    private async Task BroadcastAsync(object message)
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
            try { await c.SendAsync(text); }
            catch (Exception ex) { _log.LogDebug("drop client {id}: {msg}", c.Id, ex.Message); }
        }
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json);

    /// <summary>A single connection with a send-lock (WebSocket forbids concurrent sends).</summary>
    private sealed class Client(WebSocket socket) : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; } = socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public async Task SendAsync(string text)
        {
            if (Socket.State != WebSocketState.Open) return;
            await _sendLock.WaitAsync();
            try { await Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _sendLock.Release(); }
        }

        public void Dispose() => _sendLock.Dispose();
    }
}
