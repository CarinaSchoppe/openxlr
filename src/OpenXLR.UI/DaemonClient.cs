using System;
using System.Collections.Generic;
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

    public DaemonClient(string url = "ws://127.0.0.1:37890/ws") => _uri = new Uri(url);

    /// <summary>Raised on every state push (already on a background thread).</summary>
    public event Action<JsonNode>? StateReceived;

    /// <summary>The raw JSON of the newest state push, for diagnostics.</summary>
    public string? LastStateJson { get; private set; }

    private TaskCompletionSource<JsonNode>? _diagnosticsWaiter;

    /// <summary>Request the daemon's vendor-block dump; null on timeout.</summary>
    public async Task<JsonNode?> RequestDiagnosticsAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticsWaiter = tcs;
        await SendAsync(new Dictionary<string, object> { ["cmd"] = "getDiagnostics" });
        Task done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        _diagnosticsWaiter = null;
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    /// <summary>Raised when an error message arrives from the daemon.</summary>
    public event Action<string>? ErrorReceived;

    /// <summary>Raised on every meter frame (id to peak, 0..1 and above when clipping).</summary>
    public event Action<JsonNode>? MetersReceived;

    /// <summary>Raised when the connection comes up or goes down.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(_uri, _cts.Token);
                ConnectionChanged?.Invoke(true);
                await ReceiveLoop(_socket);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // daemon not up yet, or the link dropped, so fall through and retry
            }

            ConnectionChanged?.Invoke(false);
            _socket?.Dispose();
            _socket = null;
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
            else if (type == "diagnostics") _diagnosticsWaiter?.TrySetResult(node);
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

    /// <summary>Send the monitor mix to a different output (null disconnects).</summary>
    public Task SetMonitorOutputAsync(string? device)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "setMonitorOutput", ["device"] = device });

    /// <summary>Every output the monitor mix should feed (empty = disconnect).</summary>
    public Task SetMonitorOutputsAsync(IReadOnlyList<string> devices)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "setMonitorOutputs", ["devices"] = devices });

    /// <summary>Volume of the selected output device (0..1).</summary>
    public Task SetOutputVolumeAsync(double value)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setOutputVolume", ["value"] = value });

    /// <summary>Devices the daemon should hold as system defaults (null = don't enforce).</summary>
    public Task SetEnforcedDefaultsAsync(string? sink, string? source)
        => SendAsync(new Dictionary<string, object?>
        { ["cmd"] = "setEnforcedDefaults", ["sink"] = sink, ["source"] = source });

    /// <summary>Move an application's audio to a channel, remembered for next launch.</summary>
    /// <summary>Route an app (by identity) to a channel, silent or not.</summary>
    public Task AssignAppAsync(string identity, string channel, string? label = null)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "assignApp", ["identity"] = identity, ["channel"] = channel, ["label"] = label });

    /// <summary>Send or stop sending the Aux mix to the USB Aux port.</summary>
    public Task SetAuxPortEnabledAsync(bool on)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setAuxPortEnabled", ["value"] = on });

    /// <summary>Software low cut on the first XLR channel: 0, 80, or 120 Hz.</summary>
    public Task SetLowCutHzAsync(int hz)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setLowCutHz", ["value"] = hz });

    /// <summary>Host-side direct monitor level in percent (0..100).</summary>
    public Task SetDirectMonitorAsync(int percent)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setDirectMonitor", ["value"] = percent });

    /// <summary>Remove an app from the registry and forget its override.</summary>
    public Task ForgetAppAsync(string identity)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "forgetApp", ["identity"] = identity });

    public Task AssignStreamAsync(int streamId, string channel)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "assignStream", ["streamId"] = streamId, ["channel"] = channel });

    private async Task SendAsync(object payload)
    {
        ClientWebSocket? s = _socket;
        if (s is null || s.State != WebSocketState.Open) return;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        await _sendLock.WaitAsync();
        try { await s.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        catch (Exception) { /* dropped; the reconnect loop handles it */ }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _socket?.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
