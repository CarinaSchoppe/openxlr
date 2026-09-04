namespace OpenXLR.Core;

/// <summary>
/// The control API listens on the loopback interface only, but a web page
/// the user visits can still open a WebSocket to it: browsers send an
/// Origin header on every WebSocket handshake and nothing stopped the
/// daemon from accepting it. Native clients (the mixer window, the OpenDeck
/// plugin, scripts) send no Origin. So: no Origin is fine, an Origin on the
/// loopback host is fine (the user's own local page), anything else is not.
/// </summary>
public static class LoopbackOrigin
{
    public static bool IsAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1"
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
