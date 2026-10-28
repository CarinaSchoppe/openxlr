using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>The optional Shell extension exposes only the current focused process, on demand.</summary>
internal static class GnomeFocus
{
    internal const string Service = "org.openxlr.Focus";
    internal const string Path = "/org/openxlr/Focus";

    internal static async Task<uint> ReadAsync(CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var connection = new DBusConnection(DesktopBus.SessionAddress());
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(deadline.Token);
            uint pid = await new DesktopBus(connection).Call(Service, Path, Service, "GetFocusedProcess", null, null,
                static (message, _) => message.GetBodyReader().ReadUInt32(), deadline.Token);
            if (pid is 0 or > int.MaxValue) throw new InvalidOperationException("GNOME has no focused application window.");
            return pid;
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
            or "org.freedesktop.DBus.Error.NameHasNoOwner" or "org.freedesktop.DBus.Error.UnknownMethod")
        {
            throw new InvalidOperationException("Enable the OpenXLR Focus GNOME Shell extension to route the focused application.", ex);
        }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidOperationException("GNOME did not answer the focus query within two seconds.", ex);
        }
    }
}
