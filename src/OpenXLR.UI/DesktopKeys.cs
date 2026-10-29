using OpenXLR.UI.Localization;
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
    internal const string StopClipsKey = "clips_stop";
    internal static string ClipKey(string clip) => "clip_" + clip;

    /// <summary>A soundboard clip as its key needs it: the id, the name the desktop shows and what a press does.</summary>
    internal sealed record SoundClip(string Id, string Name, string Mode);

    private readonly SemaphoreSlim _configure = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private DBusConnection? _connection;
    private IDisposable? _activated, _deactivated, _closed;
    private int _invoking;
    private readonly Queue<(KeyAction Action, Func<bool> IsActive)> _pending = new();
    // The running session: the settings it was made from, the keys it binds,
    // and what binding them again on the same session needs.
    private DesktopKeySettings _settings = new();
    private Dictionary<string, KeyAction> _actions = new(StringComparer.Ordinal);
    private DesktopBus? _bus;
    private string? _session;
    private Func<bool>? _sessionActive;
    // The soundboard as the daemon last reported it; null until it has.
    private SoundClip[]? _library;
    // Keys that are down, so a key repeating while held is not a new press,
    // and the clips a key holds, so its release ends them. Both under _pending.
    private readonly HashSet<string> _down = new(StringComparer.Ordinal), _held = new(StringComparer.Ordinal);
    // The desktop reports keys coming up: known for Plasma, GNOME and
    // Hyprland, learned from the first Deactivated anywhere else.
    private int _releases = ReportsRelease() ? 1 : 0;
    internal string Status { get; private set; } = Localizer.Text("DesktopKeysDisabled");
    internal event Action? Changed;

    internal Task StartAsync() => ConfigureAsync(DesktopKeySettings.Load(), save: false);

    internal async Task ConfigureAsync(DesktopKeySettings settings, bool save = true)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!await _configure.WaitAsync(0))
        {
            // A configuration can sit in the desktop's permission dialog for
            // a while. Queue this one behind it rather than dropping it: the
            // window's Apply stays disabled until it has run.
            SetStatus(Localizer.Text("DesktopKeysWaiting"));
            try { await _configure.WaitAsync(_lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
        try { await ConfigureHeldAsync(settings, save); }
        finally { _configure.Release(); }
    }

    // Runs with _configure held.
    private async Task ConfigureHeldAsync(DesktopKeySettings settings, bool save)
    {
        bool replacing = false;
        try
        {
            settings = settings.Normalize();
            if (save) settings.Save(); // a failed save leaves the running session alone
            replacing = true;
            Stop();
            Volatile.Write(ref _settings, settings);
            if (!settings.Enabled) { SetStatus(Localizer.Text("DesktopKeysDisabled")); return; }
            SetStatus(Localizer.Text("DesktopKeysConnecting"));
            var connection = new DBusConnection(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? DBusAddress.Session ?? throw new InvalidOperationException(Localizer.Text("NoSessionBus")));
            _connection = connection;
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            _ = ObserveConnectionAsync(connection);
            var bus = new DesktopBus(connection);
            connection.AddMethodHandler(new DesktopFocus(bus));
            await connection.RequestNameAsync(DesktopFocus.Service).WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            var actions = Actions(settings);
            if (actions.Count == 0)
            {
                SetStatus(Localizer.Text("DesktopKeysFocusOnly"));
                return;
            }
            Volatile.Write(ref _actions, actions);
            Dictionary<string, VariantValue> created;
            try
            {
                await bus.RegisterHostAsync(_lifetime.Token);
                created = await bus.Request("CreateSession", "a{sv}", token => (ref MessageWriter w) =>
                    w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token, ["session_handle_token"] = token }), _lifetime.Token);
            }
            catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.UnknownMethod"
                or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.ServiceUnknown")
            {
                // No shortcut session exists. Keep the independent focus
                // service available to Deck instead of closing its bus too.
                SetStatus(Localizer.Text("DesktopKeysFocusOnly") + " " + Localizer.Text("DesktopKeysShortcutsUnavailable"));
                return;
            }
            string session = created["session_handle"].GetString();
            int active = 0; // 0 binding, 1 active, -1 closed
            bool IsCurrent() => ReferenceEquals(_connection, connection) && !_lifetime.IsCancellationRequested;
            Func<bool> isActive = () => IsCurrent() && Volatile.Read(ref active) == 1;
            _activated = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = DesktopBus.PortalPath, Interface = DesktopBus.Shortcuts, Member = "Activated" },
                static (message, _) => { var r = message.GetBodyReader(); return (r.ReadObjectPath().ToString(), r.ReadString()); },
                notification =>
                {
                    if (!IsCurrent()) return;
                    if (notification.IsCompletion)
                    {
                        Interlocked.Exchange(ref active, -1);
                        ReleaseHeld();
                        SetStatus(Localizer.Text("DesktopConnectionLost"));
                        return;
                    }
                    var (activeSession, id) = notification.Value;
                    if (isActive() && activeSession == session
                        && Volatile.Read(ref _actions).TryGetValue(id, out KeyAction? action))
                    {
                        if (action.Clip is { } clip) PressClip(id, clip, action, isActive);
                        else _ = InvokeAsync(action, isActive);
                    }
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            _deactivated = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = DesktopBus.PortalPath, Interface = DesktopBus.Shortcuts, Member = "Deactivated" },
                static (message, _) => { var r = message.GetBodyReader(); return (r.ReadObjectPath().ToString(), r.ReadString()); },
                notification =>
                {
                    // The Activated observer reports a lost connection.
                    if (notification.IsCompletion || !isActive()) return;
                    var (activeSession, id) = notification.Value;
                    if (activeSession != session) return;
                    Volatile.Write(ref _releases, 1);
                    lock (_pending) _down.Remove(id);
                    if (Volatile.Read(ref _actions).TryGetValue(id, out KeyAction? action) && action.Clip is { } clip)
                        _ = InvokeAsync(ReleaseAction(action, clip), isActive, always: true);
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            _closed = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = session, Interface = "org.freedesktop.portal.Session", Member = "Closed" },
                static (_, _) => true, _ =>
                {
                    Interlocked.Exchange(ref active, -1);
                    if (!IsCurrent()) return;
                    ReleaseHeld();
                    SetStatus(Localizer.Text("ShortcutSessionClosed"));
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            await BindAsync(bus, session, actions);
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                throw new InvalidOperationException(Localizer.Text("ShortcutSessionClosedWhileConfiguring"));
            _bus = bus;
            _session = session;
            _sessionActive = isActive;
            SetStatus(Localizer.Text("DesktopKeysActive"));
            if (save)
            {
                try
                {
                    await bus.Empty(DesktopBus.Portal, DesktopBus.PortalPath, DesktopBus.Shortcuts,
                        "ConfigureShortcuts", "osa{sv}", (ref MessageWriter w) =>
                        {
                            w.WriteObjectPath(session);
                            w.WriteString("");
                            w.WriteDictionary(new Dictionary<string, VariantValue>());
                        }, _lifetime.Token);
                }
                catch (DBusErrorReplyException ex) when (ex.ErrorName is
                    "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownInterface")
                {
                    if (isActive()) SetStatus(Localizer.Text("DesktopKeysActive") + " " + Localizer.Text("DesktopKeysConfigureUnsupported"));
                }
                catch (Exception ex)
                {
                    // The keys are already bound. An editor failure must not
                    // tear down a working session or hide the opening error.
                    if (IsCurrent()) SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            if (replacing) Stop();
            SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
        }
    }

    private Task BindAsync(DesktopBus bus, string session, Dictionary<string, KeyAction> actions)
        => bus.Request("BindShortcuts", "oa(sa{sv})sa{sv}", token => (ref MessageWriter w) =>
        {
            w.WriteObjectPath(session);
            var array = w.WriteArrayStart(DBusType.Struct);
            foreach (var (id, action) in actions)
            {
                w.WriteStructureStart(); w.WriteString(id);
                var properties = new Dictionary<string, VariantValue> { ["description"] = action.Description };
                if (action.PreferredTrigger is { } trigger) properties["preferred_trigger"] = trigger;
                w.WriteDictionary(properties);
            }
            w.WriteArrayEnd(array);
            w.WriteString(""); // the portal supplies its own permission/configuration window
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        }, _lifetime.Token);

    /// <summary>
    /// The soundboard as the daemon reports it, on every state that carries
    /// one. A clip added, renamed or deleted changes the keys bound for the
    /// clips the user chose; a mode change only changes what the next press does.
    /// </summary>
    internal void SetClips(IEnumerable<SoundClip> clips)
    {
        SoundClip[] next = [.. clips.Where(c => DesktopKeySettings.ValidClip(c.Id)).Select(c => c with
        {
            Name = new string([.. (c.Name ?? "").Where(ch => !char.IsControl(ch))]).Trim() is { Length: > 0 } name
                ? name[..Math.Min(name.Length, 60)] : c.Id,
        })];
        SoundClip[]? previous = Volatile.Read(ref _library);
        if (previous is not null && previous.SequenceEqual(next)) return;
        Volatile.Write(ref _library, next);
        if (Volatile.Read(ref _settings) is { Enabled: true, Clips.Count: > 0 }) _ = RebindAsync();
    }

    /// <summary>
    /// Bind the chosen clips again after the library changed. The session
    /// that runs binds the new list itself, so its keys keep working while
    /// the desktop asks about a new one; KDE drops the keys of a clip left
    /// out and keeps every other. A desktop that binds a session only once
    /// gets a new session, as Apply makes one.
    /// </summary>
    private async Task RebindAsync()
    {
        try { await _configure.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            DesktopKeySettings settings = Volatile.Read(ref _settings);
            if (!settings.Enabled) return;
            var actions = Actions(settings);
            if (SameBinding(actions, Volatile.Read(ref _actions))) return;
            DesktopBus? bus = _bus;
            string? session = _session;
            Func<bool>? active = _sessionActive;
            // With no session running, the next start or Apply binds the list.
            if (bus is null || session is null || active is null || !active()) return;
            if (actions.Count == 0)
            {
                await ConfigureHeldAsync(settings, save: false);
                return;
            }
            try
            {
                await BindAsync(bus, session, actions);
                Volatile.Write(ref _actions, actions);
                if (active()) SetStatus(Localizer.Text("DesktopKeysActive"));
            }
            catch (Exception ex) when (ex is DBusErrorReplyException or PortalResponseException { Code: 2 }
                && !_lifetime.IsCancellationRequested && active())
            {
                await ConfigureHeldAsync(settings, save: false);
            }
            catch (Exception ex) when (!_lifetime.IsCancellationRequested)
            {
                // Cancelled or unanswered: the keys bound before keep working.
                if (active()) SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
            }
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested) { SetStatus(Localizer.Format("DesktopKeysError", ex.Message)); }
        catch (Exception) { /* the UI is closing */ }
        finally { _configure.Release(); }
    }

    private static bool SameBinding(Dictionary<string, KeyAction> next, Dictionary<string, KeyAction> bound)
        => next.Count == bound.Count && next.All(pair => bound.TryGetValue(pair.Key, out KeyAction? old) && old.Description == pair.Value.Description);

    /// <summary>The desktops whose shortcut portal sends Deactivated when a key comes up.</summary>
    internal static bool ReportsRelease(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        string? current = env("XDG_CURRENT_DESKTOP");
        string desktop = string.IsNullOrWhiteSpace(current) ? env("XDG_SESSION_DESKTOP") ?? "" : current;
        return desktop.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name => name.Equals("KDE", StringComparison.OrdinalIgnoreCase) || name.Equals("Plasma", StringComparison.OrdinalIgnoreCase)
                || name.Equals("GNOME", StringComparison.OrdinalIgnoreCase) || name.Equals("Hyprland", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The desktop has reported keys coming up, or is known to.</summary>
    internal bool DeliversRelease => Volatile.Read(ref _releases) == 1;

    /// <param name="Clip">The soundboard clip a press plays, or null.</param>
    /// <param name="Quiet">Success leaves the status line as it was.</param>
    internal sealed record KeyAction(string Description, Func<Task<string?>> Invoke, string? PreferredTrigger = null,
        string? Clip = null, bool Quiet = false);
    internal Dictionary<string, KeyAction> Actions(DesktopKeySettings settings)
    {
        var actions = new Dictionary<string, KeyAction>(StringComparer.Ordinal);
        foreach (string channel in settings.FocusChannels)
            actions["focus_" + channel] = new(Localizer.Format("KeyRouteFocusedApp", channel), () => client.RouteFocusedAppAsync(channel));
        if (settings.MonitorControls)
            actions["monitor_mute"] = new(Localizer.Text("KeyToggleMonitoringPause"), client.ToggleMonitorMutedAsync, "XF86AudioMute");
        if (settings.OutputControls)
        {
            actions["output_up"] = new(Localizer.Text("KeyOutputVolumeUp"), () => client.AdjustOutputVolumeAsync(settings.OutputDevice, .05));
            actions["output_down"] = new(Localizer.Text("KeyOutputVolumeDown"), () => client.AdjustOutputVolumeAsync(settings.OutputDevice, -.05));
            actions["output_mute"] = new(Localizer.Text("KeyToggleOutputMute"), () => client.ToggleOutputMuteAsync(settings.OutputDevice));
        }
        foreach (string output in settings.MainOutputs)
            actions[DesktopKeySettings.MainKey(output)] = new(Localizer.Format("KeySetSystemOutput", output), () => client.SetMainOutputAsync(output));
        if (settings.Clips.Count > 0)
        {
            // Until the daemon reports the board, every chosen clip keeps its
            // key under its id, so a start before the daemon drops no binding.
            // Once it has, a clip it no longer lists was deleted.
            SoundClip[]? library = Volatile.Read(ref _library);
            var chosen = settings.Clips.ToHashSet(StringComparer.Ordinal);
            IEnumerable<SoundClip> clips = library is null
                ? settings.Clips.Select(id => new SoundClip(id, id, "overlap"))
                : library.Where(c => chosen.Contains(c.Id));
            foreach (SoundClip clip in clips)
                actions[ClipKey(clip.Id)] = new(Localizer.Format("KeyPlayClip", clip.Name),
                    () => client.PlayClipAsync(clip.Id, Mode(clip.Id, hold: false)), Clip: clip.Id);
        }
        if (settings.ClipsStop) actions[StopClipsKey] = new(Localizer.Text("KeyStopClips"), client.StopClipsAsync);
        return actions;
    }

    /// <summary>
    /// The mode a key press sends: the clip's own, said explicitly so that a
    /// hold the key could never end is not made. Hold plays as overlap
    /// without a release to end it.
    /// </summary>
    private string Mode(string clip, bool hold)
    {
        string mode = Volatile.Read(ref _library)?.FirstOrDefault(c => c.Id == clip)?.Mode ?? "overlap";
        return mode is "overlap" or "restart" or "toggle" || (mode == "hold" && hold) ? mode : "overlap";
    }

    private void PressClip(string id, string clip, KeyAction action, Func<bool> isActive)
    {
        bool releases = DeliversRelease;
        // A key held down may repeat; only its first press plays.
        lock (_pending) if (releases && !_down.Add(id)) return;
        string mode = Mode(clip, hold: releases);
        SoundClip? entry = Volatile.Read(ref _library)?.FirstOrDefault(c => c.Id == clip);
        KeyAction press = mode == "hold" ? action with { Invoke = () => HoldAsync(clip, isActive) }
            : entry?.Mode == "hold" ? action with { Invoke = async () => await client.PlayClipAsync(clip, mode)
                ?? Localizer.Format("DesktopKeyClipNoRelease", entry.Name) }
            : action with { Invoke = () => client.PlayClipAsync(clip, mode) };
        _ = InvokeAsync(press, isActive);
    }

    private async Task<string?> HoldAsync(string clip, Func<bool> isActive)
    {
        string? error = await client.PlayClipAsync(clip, "hold");
        if (error is not null) return error;
        bool ended;
        lock (_pending)
        {
            ended = !isActive();
            if (!ended) _held.Add(clip);
        }
        // The session ended while the press was on its way: nothing will
        // report the key coming up, so the hold ends here.
        if (ended) await client.ReleaseClipAsync(clip);
        return null;
    }

    private KeyAction ReleaseAction(KeyAction action, string clip) => action with
    {
        Quiet = true,
        Invoke = async () =>
        {
            bool held;
            lock (_pending) held = _held.Remove(clip);
            return held ? await client.ReleaseClipAsync(clip) : null;
        },
    };

    /// <summary>The session ended with keys down: the daemon ends their holds, since no release will come.</summary>
    private void ReleaseHeld()
    {
        string[] held;
        lock (_pending)
        {
            held = [.. _held];
            _held.Clear();
            _down.Clear();
        }
        foreach (string clip in held) _ = client.ReleaseClipAsync(clip);
    }

    private async Task ObserveConnectionAsync(DBusConnection connection)
    {
        await connection.DisconnectedAsync().ConfigureAwait(false);
        if (ReferenceEquals(_connection, connection) && !_lifetime.IsCancellationRequested)
            SetStatus(Localizer.Text("DesktopConnectionLost"));
    }

    /// <param name="always">A release, which is queued past the bound: a held clip must hear its key come up.</param>
    private async Task InvokeAsync(KeyAction action, Func<bool> isActive, bool always = false)
    {
        bool full;
        lock (_pending)
        {
            if (!isActive()) return;
            full = !always && _pending.Count >= 16;
            if (!full)
            {
                _pending.Enqueue((action, isActive));
                if (_invoking != 0) return;
                Volatile.Write(ref _invoking, 1);
            }
        }
        if (full)
        {
            if (isActive()) SetStatus(Localizer.Text("DesktopKeyQueueFull"));
            return;
        }
        // One consumer preserves output-switch/volume order without losing
        // ordinary key repeats or accumulating unbounded daemon requests.
        while (true)
        {
            lock (_pending)
            {
                if (_pending.Count == 0) { Volatile.Write(ref _invoking, 0); return; }
                (action, isActive) = _pending.Dequeue();
            }
            if (!isActive()) continue;
            try
            {
                string? error = await action.Invoke();
                if (isActive() && (error is not null || !action.Quiet))
                    SetStatus(error ?? Localizer.Format("DesktopKeyCompleted", action.Description));
            }
            catch (Exception ex) { if (isActive()) SetStatus(ex.Message); }
        }
    }

    private void SetStatus(string text) { Status = text; Changed?.Invoke(); }
    private void Stop()
    {
        DBusConnection? connection = _connection;
        _connection = null; // invalidate queued callbacks before disposing their observers
        _bus = null; _session = null; _sessionActive = null;
        Volatile.Write(ref _actions, new Dictionary<string, KeyAction>(StringComparer.Ordinal));
        lock (_pending) _pending.Clear();
        ReleaseHeld();
        _activated?.Dispose(); _activated = null;
        _deactivated?.Dispose(); _deactivated = null;
        _closed?.Dispose(); _closed = null;
        // A portal session is owned by this connection and closes with it.
        connection?.Dispose();
    }
    public void Dispose() { _lifetime.Cancel(); Stop(); }
}
