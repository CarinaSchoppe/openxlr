using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// The UI's link to the daemon: one WebSocket, reconnecting on its own, raising
/// <see cref="StateReceived"/> for every pushed state. State is handed over as a
/// JsonNode so the UI can bind to it without duplicating the daemon's records.
/// </summary>
public sealed class DaemonClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Uri _uri;
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _lifecycle = new();
    private Task? _runTask;
    private bool _disposed;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _queries = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _commands = new();

    public DaemonClient(string url = "ws://127.0.0.1:37890/ws") => _uri = new Uri(url);

    /// <summary>Raised on every state push (already on a background thread).</summary>
    public event Action<JsonNode>? StateReceived;

    /// <summary>The raw JSON of the newest state push, for diagnostics.</summary>
    public string? LastStateJson { get; private set; }

    /// <summary>Request the daemon's vendor-block dump; null on timeout.</summary>
    public Task<JsonNode?> RequestDiagnosticsAsync(TimeSpan timeout)
        => QueryAsync("diagnostics", "getDiagnostics", timeout);

    /// <summary>Request the daemon's plugin catalog (the "plugins" array); null on timeout.</summary>
    public Task<JsonNode?> RequestPluginsAsync(TimeSpan timeout)
        => QueryAsync("plugins", "listPlugins", timeout);

    /// <summary>Request reusable preset names from the daemon.</summary>
    public Task<JsonNode?> RequestPresetsAsync(TimeSpan timeout)
        => QueryAsync("presets", "listPresets", timeout);

    private async Task<JsonNode?> QueryAsync(string type, string command, TimeSpan timeout)
    {
        var proposed = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = _queries.GetOrAdd(type, proposed);
        try
        {
            if (ReferenceEquals(waiter, proposed) && !await SendAsync(new { cmd = command }, reportErrors: false))
                waiter.TrySetResult(null);
            return await waiter.Task.WaitAsync(timeout);
        }
        catch (TimeoutException) { return null; }
        finally { _queries.TryRemove(new KeyValuePair<string, TaskCompletionSource<JsonNode?>>(type, waiter)); }
    }

    /// <summary>Raised when an error message arrives from the daemon.</summary>
    public event Action<string>? ErrorReceived;

    /// <summary>Raised on every meter frame (id to peak, 0..1 and above when clipping).</summary>
    public event Action<JsonNode>? MetersReceived;

    /// <summary>Raised when the connection comes up or goes down.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runTask ??= RunAsync();
        }
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var socket = new ClientWebSocket();
                _socket = socket;
                // Detect a frozen peer as well as a closed socket, without a
                // timer or network operation on the Avalonia dispatcher.
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(_uri, connect.Token);
                ConnectionChanged?.Invoke(true);
                await ReceiveLoop(socket);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // daemon not up yet, or the link dropped, so fall through and retry
            }
            finally
            {
                _socket?.Dispose();
                _socket = null;
                foreach (var query in _queries.Values) query.TrySetResult(null);
                _queries.Clear();
                foreach (var command in _commands.Values)
                    command.TrySetResult("Connection lost. Check the restored state before retrying.");
                ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(1000, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket socket)
    {
        var buf = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await socket.ReceiveAsync(buf, _cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            string text = Encoding.UTF8.GetString(ms.ToArray());
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (JsonException) { continue; }
            if (node is null) continue;

            string? type = node["type"]?.GetValue<string>();
            if (type == "error") ErrorReceived?.Invoke(node["message"]?.GetValue<string>() ?? "unknown error");
            else if (type == "state") { LastStateJson = text; StateReceived?.Invoke(node); }
            else if (type == "diagnostics" && _queries.TryGetValue(type, out var diagnostics)) diagnostics.TrySetResult(node);
            else if (type == "plugins" && _queries.TryGetValue(type, out var plugins)) plugins.TrySetResult(node["plugins"]);
            else if (type == "presets" && _queries.TryGetValue(type, out var presets)) presets.TrySetResult(node);
            else if (type == "commandResult" && node["requestId"]?.GetValue<string>() is string id
                     && _commands.TryGetValue(id, out var command))
                command.TrySetResult(node["error"]?.GetValue<string>());
            else if (type == "meters" && node["levels"] is JsonNode levels) MetersReceived?.Invoke(levels);
        }
    }

    /// <summary>Set a hardware control (gain, mute, lowCut, …).</summary>
    public Task SetActiveDeviceAsync(string usbId)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setActiveDevice", ["device"] = usbId });

    public Task SaveProfileAsync(string name)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "saveProfile", ["name"] = name });

    public Task LoadProfileAsync(string name)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "loadProfile", ["name"] = name });

    public Task DeleteProfileAsync(string name)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "deleteProfile", ["name"] = name });

    public Task SetControlAsync(string control, object value)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "set", ["control"] = control, ["value"] = value });

    public Task SetLevelAsync(string channel, string mix, double value)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setLevel", ["channel"] = channel, ["mix"] = mix, ["value"] = value });

    public Task SetChannelMutedAsync(string channel, string mix, bool muted)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setChannelMuted", ["channel"] = channel, ["mix"] = mix, ["value"] = muted });

    public Task SetMixVolumeAsync(string mix, double value)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setMixVolume", ["mix"] = mix, ["value"] = value });

    public Task SetMixMutedAsync(string mix, bool muted)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setMixMuted", ["mix"] = mix, ["value"] = muted });

    public Task<string?> CreateChannelAsync(string name)
        => SendConfirmedAsync(new() { ["cmd"] = "createChannel", ["name"] = name });

    public Task<string?> RenameChannelAsync(string channel, string name)
        => SendConfirmedAsync(new() { ["cmd"] = "renameChannel", ["channel"] = channel, ["name"] = name });

    public Task<string?> DeleteChannelAsync(string channel)
        => SendConfirmedAsync(new() { ["cmd"] = "deleteChannel", ["channel"] = channel });

    public Task<string?> ReorderChannelsAsync(IReadOnlyList<string> order)
        => SendConfirmedAsync(new() { ["cmd"] = "reorderChannels", ["order"] = order });

    public Task<string?> CreateMixAsync(string name)
        => SendConfirmedAsync(new() { ["cmd"] = "createMix", ["name"] = name });

    public Task<string?> RenameMixAsync(string mix, string name)
        => SendConfirmedAsync(new() { ["cmd"] = "renameMix", ["mix"] = mix, ["name"] = name });

    public Task<string?> DeleteMixAsync(string mix)
        => SendConfirmedAsync(new() { ["cmd"] = "deleteMix", ["mix"] = mix });

    public Task<string?> ReorderMixesAsync(IReadOnlyList<string> order)
        => SendConfirmedAsync(new() { ["cmd"] = "reorderMixes", ["order"] = order });

    public Task<string?> SetMonitoredMixAsync(string mix)
        => SendConfirmedAsync(new() { ["cmd"] = "setMonitoredMix", ["mix"] = mix });

    private async Task<string?> SendConfirmedAsync(Dictionary<string, object> payload,
        TimeSpan? timeout = null)
    {
        string id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands[id] = waiter;
        payload["requestId"] = id;
        try
        {
            if (!await SendAsync(payload)) return "Daemon disconnected; no change was sent.";
            return await waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException) { return "No confirmation from daemon. Check its state before retrying."; }
        finally { _commands.TryRemove(id, out _); }
    }

    /// <summary>Send the listened mix to a different output (null disconnects).</summary>
    public Task SetMonitorOutputAsync(string? device)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "setMonitorOutput", ["device"] = device });

    /// <summary>Every output the listened mix should feed (empty = disconnect).</summary>
    public Task SetMonitorOutputsAsync(IReadOnlyList<string> devices)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "setMonitorOutputs", ["devices"] = devices });

    /// <summary>Atomically mutate one cell of the many-to-many routing matrix.</summary>
    public Task<string?> SetOutputRouteAsync(string mix, string destinationId, bool enabled,
        string stage = "MixProcessed")
        => SendConfirmedAsync(new()
        {
            ["cmd"] = "setOutputRoute",
            ["mix"] = mix,
            ["destinationId"] = destinationId,
            ["enabled"] = enabled,
            ["stage"] = stage,
        });

    /// <summary>Volume of the selected output device (0..1).</summary>
    public Task SetOutputVolumeAsync(double value)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setOutputVolume", ["value"] = value });

    /// <summary>Devices the daemon should hold as system defaults (null = don't enforce).</summary>
    public Task SetEnforcedDefaultsAsync(string? sink, string? source)
        => SendAsync(new Dictionary<string, object?>
        { ["cmd"] = "setEnforcedDefaults", ["sink"] = sink, ["source"] = source });

    /// <summary>Move an application's audio to a channel, remembered for next launch.</summary>
    public Task AssignAppAsync(string identity, string channel, string? label = null)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "assignApp", ["identity"] = identity, ["channel"] = channel, ["label"] = label });

    /// <summary>Send or stop sending the Aux mix to the USB Aux port.</summary>
    public Task SetAuxPortEnabledAsync(bool on)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setAuxPortEnabled", ["value"] = on });

    /// <summary>Software low cut on the first XLR channel: 0, 80, or 120 Hz.</summary>
    public Task SetLowCutHzAsync(int hz)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setLowCutHz", ["value"] = hz });

    /// <summary>Software ClipGuard (host-side limiter) on or off.</summary>
    public Task SetSoftClipGuardAsync(bool on)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setSoftClipGuard", ["value"] = on });

    /// <summary>Replace a channel's plugin insert chain (ordered).</summary>
    public Task SetInsertsAsync(string channel, IReadOnlyList<object> inserts)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setInserts", ["channel"] = channel, ["inserts"] = inserts });

    public Task<string?> ShowInsertUiAsync(string channel, string insertId)
        => SendConfirmedAsync(new() { ["cmd"] = "showInsertUi", ["channel"] = channel, ["insertId"] = insertId });

    public Task SetInsertBypassAsync(string channel, string insertId, bool bypass)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setInsertBypass", ["channel"] = channel, ["insertId"] = insertId, ["value"] = bypass });

    public Task SetInsertParamAsync(string channel, string insertId, string symbol, double value)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setInsertParam", ["channel"] = channel, ["insertId"] = insertId, ["symbol"] = symbol, ["value"] = value });

    public Task<string?> RetryInsertHostAsync(string channel, string insertId, bool clearQuarantine)
        => SendConfirmedAsync(new()
        {
            ["cmd"] = clearQuarantine ? "unquarantineInsert" : "retryInsertHost",
            ["channel"] = channel,
            ["insertId"] = insertId,
        });

    public Task<string?> RescanPluginsAsync()
        => SendConfirmedAsync(new() { ["cmd"] = "rescanPlugins" }, TimeSpan.FromMinutes(5));

    public Task<string?> RetryPluginAsync(string pluginId, bool clearQuarantine)
        => SendConfirmedAsync(new()
        {
            ["cmd"] = clearQuarantine ? "unquarantinePlugin" : "retryPlugin",
            ["plugin"] = pluginId,
        }, TimeSpan.FromSeconds(30));

    public Task<string?> SaveChainPresetAsync(string channel, string name)
        => SendConfirmedAsync(new() { ["cmd"] = "saveChainPreset", ["channel"] = channel, ["name"] = name });

    public Task<string?> LoadChainPresetAsync(string channel, string name)
        => SendConfirmedAsync(new() { ["cmd"] = "loadChainPreset", ["channel"] = channel, ["name"] = name });

    public Task<string?> DeletePresetAsync(string kind, string name)
        => SendConfirmedAsync(new() { ["cmd"] = "deletePreset", ["presetKind"] = kind, ["name"] = name });

    public Task<string?> DuplicatePresetAsync(string kind, string name, string newName)
        => SendConfirmedAsync(new()
        {
            ["cmd"] = "duplicatePreset",
            ["presetKind"] = kind,
            ["name"] = name,
            ["newName"] = newName,
        });

    public Task<string?> SavePluginPresetAsync(string channel, string insertId, string name)
        => SendConfirmedAsync(new()
        {
            ["cmd"] = "savePluginPreset",
            ["channel"] = channel,
            ["insertId"] = insertId,
            ["name"] = name,
        });

    public Task<string?> LoadPluginPresetAsync(string channel, string insertId, string name)
        => SendConfirmedAsync(new()
        {
            ["cmd"] = "loadPluginPreset",
            ["channel"] = channel,
            ["insertId"] = insertId,
            ["name"] = name,
        });

    /// <summary>Remove an app from the registry and forget its override.</summary>
    public Task ForgetAppAsync(string identity)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "forgetApp", ["identity"] = identity });

    public Task AssignStreamAsync(int streamId, string channel)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "assignStream", ["streamId"] = streamId, ["channel"] = channel });

    private async Task<bool> SendAsync(object payload, bool reportErrors = true)
    {
        ClientWebSocket? s = _socket;
        if (_disposed || s is null || s.State != WebSocketState.Open)
        {
            if (reportErrors) ErrorReceived?.Invoke("Daemon disconnected; no change was sent.");
            return false;
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        bool acquired = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _sendLock.WaitAsync(timeout.Token);
            acquired = true;
            await s.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (reportErrors) ErrorReceived?.Invoke("Connection lost; the change could not be confirmed.");
            return false;
        }
        finally { if (acquired) _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await _cts.CancelAsync();
        if (_runTask is not null) await _runTask;
        // Sends may still be unwinding their finally blocks. SemaphoreSlim has
        // no native handle here and is collected with the client, not disposed
        // underneath those continuations.
        _cts.Dispose();
    }
}
