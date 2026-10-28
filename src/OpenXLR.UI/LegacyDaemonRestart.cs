using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// The one request this release sends to a daemon from before the control
/// socket: restartDaemon over that daemon's loopback WebSocket, after the
/// token it wrote. Only the Flatpak UI attached to a daemon it did not start
/// uses it. That daemon runs under the user service in another sandbox
/// instance, and the request is the only way to reach it from this one;
/// the service then starts the updated daemon. Native installations restart
/// the unit with systemctl instead.
/// </summary>
internal static class LegacyDaemonRestart
{
    internal static bool Request(int port = DaemonProbe.LegacyPort, string? tokenPath = null)
    {
        try { return RequestAsync(port, tokenPath ?? OpenXlrPaths.LegacyTokenPath).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WebSocketException
            or OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<bool> RequestAsync(int port, string tokenPath)
    {
        // The older daemon wrote 64 hex digits and a newline into a private file.
        var file = new FileInfo(tokenPath);
        if (!file.Exists || file.LinkTarget is not null || file.Length > 256) return false;
        string token = RegularFile.ReadText(tokenPath, 256).Trim();
        if (token.Length != 64) return false;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), timeout.Token);
        foreach (object message in new object[] { new { cmd = "auth", token }, new { cmd = "restartDaemon" } })
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)),
                WebSocketMessageType.Text, true, timeout.Token);
        // The daemon exits once it has read the request; wait for that close,
        // reading past the state it sends first, so the request is not cut
        // off by our own close. A refused token closes with a policy violation.
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return result.CloseStatus != WebSocketCloseStatus.PolicyViolation;
            }
        }
        catch (WebSocketException) when (!timeout.IsCancellationRequested) { /* the daemon went away */ }
        return true;
    }
}
