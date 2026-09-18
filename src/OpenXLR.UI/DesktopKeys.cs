using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Global keys live with the UI, including while it is hidden in the tray.</summary>
internal sealed class DesktopKeys(DaemonClient client) : IDisposable
{
    private readonly SemaphoreSlim _configure = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private DBusConnection? _connection;
    private IDisposable? _activated, _closed;
    private int _invoking;
    internal string Status { get; private set; } = "Desktop keys are disabled.";
    internal event Action? Changed;

    internal Task StartAsync() => ConfigureAsync(DesktopKeySettings.Load(), save: false);

    internal async Task ConfigureAsync(DesktopKeySettings settings, bool save = true)
    {
        if (_lifetime.IsCancellationRequested || !await _configure.WaitAsync(0)) return;
        bool replacing = false;
        try
        {
            settings = settings.Normalize();
            if (save) settings.Save(); // a failed save leaves the running session alone
            replacing = true;
            Stop();
            if (!settings.Enabled) { SetStatus("Desktop keys are disabled."); return; }
            SetStatus("Connecting to the desktop...");
            var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus is available."));
            _connection = connection;
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            var bus = new DesktopBus(connection);
            connection.AddMethodHandler(new KWinFocus(bus));
            await connection.RequestNameAsync(KWinFocus.Service).WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            if (settings.FocusChannels.Count == 0)
            {
                SetStatus("OpenDeck focus routing is enabled. Select channels below to assign PC shortcuts.");
                return;
            }
            var created = await bus.Request("CreateSession", "a{sv}", token => (ref MessageWriter w) =>
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token, ["session_handle_token"] = token }), _lifetime.Token);
            string session = created["session_handle"].GetString();
            var channels = settings.FocusChannels.ToHashSet(StringComparer.Ordinal);
            int active = 0; // 0 binding, 1 active, -1 closed
            _activated = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = DesktopBus.PortalPath, Interface = DesktopBus.Shortcuts, Member = "Activated" },
                static (message, _) => { var r = message.GetBodyReader(); return (r.ReadObjectPath().ToString(), r.ReadString()); },
                notification =>
                {
                    if (notification.IsCompletion) { SetStatus("Desktop connection lost. Open Desktop keys and apply to reconnect."); return; }
                    var (activeSession, id) = notification.Value;
                    if (Volatile.Read(ref active) == 1 && ReferenceEquals(_connection, connection) && activeSession == session && id.StartsWith("focus_", StringComparison.Ordinal)
                        && channels.Contains(id[6..])) _ = InvokeAsync(id[6..]);
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            _closed = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = session, Interface = "org.freedesktop.portal.Session", Member = "Closed" },
                static (_, _) => true, _ =>
                {
                    Interlocked.Exchange(ref active, -1);
                    SetStatus("Shortcut session closed. Open Desktop keys and apply to reconnect.");
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            await bus.Request("BindShortcuts", "oa(sa{sv})sa{sv}", token => (ref MessageWriter w) =>
            {
                w.WriteObjectPath(session);
                var array = w.WriteArrayStart(DBusType.Struct);
                foreach (string channel in settings.FocusChannels)
                {
                    w.WriteStructureStart(); w.WriteString("focus_" + channel);
                    w.WriteDictionary(new Dictionary<string, VariantValue> { ["description"] = "Route focused app to " + channel });
                }
                w.WriteArrayEnd(array);
                w.WriteString(""); // the portal supplies its own permission/configuration window
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
            }, _lifetime.Token);
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                throw new InvalidOperationException("The desktop closed the shortcut session while it was being configured.");
            SetStatus("Desktop keys are active. Keep OpenXLR running, including in the tray.");
        }
        catch (Exception ex)
        {
            if (replacing) Stop();
            SetStatus("Desktop keys: " + ex.Message);
        }
        finally { _configure.Release(); }
    }

    private async Task InvokeAsync(string channel)
    {
        if (Interlocked.Exchange(ref _invoking, 1) != 0) return;
        try
        {
            string? error = await client.RouteFocusedAppAsync(channel);
            SetStatus(error ?? "Focused application routed to " + channel + ".");
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { Volatile.Write(ref _invoking, 0); }
    }

    private void SetStatus(string text) { Status = text; Changed?.Invoke(); }
    private void Stop()
    {
        _activated?.Dispose(); _activated = null;
        _closed?.Dispose(); _closed = null;
        // A portal session is owned by this connection and closes with it.
        _connection?.Dispose(); _connection = null;
    }
    public void Dispose() { _lifetime.Cancel(); Stop(); }
}
