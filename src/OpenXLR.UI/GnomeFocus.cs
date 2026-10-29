using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Read focus on the connection owning org.openxlr.Desktop.</summary>
internal sealed class GnomeFocus(DesktopBus bus)
{
    internal const string Service = "org.openxlr.Focus";
    internal const string Path = "/org/openxlr/Focus";
    private readonly object _gate = new();
    private Task<uint?>? _pending;

    internal async Task<uint> ReadAsync(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Task<uint?> pending;
        lock (_gate)
        {
            if (_pending is { IsCompleted: false })
                throw new InvalidOperationException("GNOME is still answering the previous focus query; try again after it recovers.");
            _pending = pending = BeginRead();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            uint? pid = await pending.WaitAsync(deadline.Token);
            if (pid is null) throw new InvalidOperationException("GNOME returned an invalid focus reply.");
            if (pid is 0 or > int.MaxValue) throw new InvalidOperationException("GNOME has no focused application window.");
            return pid.Value;
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
            or "org.freedesktop.DBus.Error.NameHasNoOwner" or "org.freedesktop.DBus.Error.UnknownMethod"
            or "org.freedesktop.DBus.Error.UnknownObject")
        {
            throw new InvalidOperationException("Enable the OpenXLR Focus GNOME Shell extension to route the focused application.", ex);
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.DBus.Error.AccessDenied")
        {
            throw new InvalidOperationException("GNOME focus is restricted to the running OpenXLR desktop service; enable Desktop keys and retry.", ex);
        }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidOperationException("GNOME did not answer the focus query within two seconds.", ex);
        }
    }

    private Task<uint?> BeginRead()
    {
        using var writer = bus.Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, Path, Service, "GetFocusedProcess");
        Task<uint?> pending = bus.Connection.CallMethodAsync<uint?>(writer.CreateMessage(), static (message, _) =>
            message.SignatureAsString == "u" ? message.GetBodyReader().ReadUInt32() : null);
        // This must keep the UI's bus owner. The protocol cannot cancel a
        // single reply, so retain at most one and observe a late failure.
        // A later query always asks again rather than reusing a stale PID.
        _ = pending.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return pending;
    }
}
