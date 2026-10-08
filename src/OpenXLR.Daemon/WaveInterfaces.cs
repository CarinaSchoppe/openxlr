using System.Collections.Concurrent;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Daemon;

public sealed record WaveInterfaceState(string Id, string Name, bool Active, bool Enabled,
    bool Connected, string CaptureHint, DeviceCapabilities Capabilities, DeviceState? State, string? Warning);

/// <summary>
/// Additional interfaces reuse the same device manager, USB isolation and reconnect
/// policy as the primary interface. Each has its own exact USB address and state ID.
/// </summary>
public sealed class WaveInterfaces : BackgroundService
{
    private readonly DeviceManager _primary;
    private readonly ILogger<DeviceManager> _deviceLog;
    private readonly IConfiguration _configuration;
    private readonly Func<IReadOnlyList<IAudioDevice>> _detect;
    private readonly object _preferencesGate = new();
    private readonly ConcurrentDictionary<string, DeviceManager> _sessions = new(StringComparer.Ordinal);
    private IReadOnlyList<IAudioDevice> _available = [];
    private HashSet<string> _enabled = new(StringComparer.Ordinal);
    private string? _reserved;
    private string? _error;
    private string? _discoveryError;
    private volatile bool _stopping;
    private readonly object _sessionsGate = new();
    private const int MaximumAdditional = 4;
    private static string PathName => OpenXlrPaths.ConfigFile("wave-interfaces.json");
    public event Action? Changed;
    public string? Warning => _error ?? _discoveryError;

    public WaveInterfaces(DeviceManager primary, ILogger<DeviceManager> deviceLog, IConfiguration configuration)
        : this(primary, deviceLog, configuration, DeviceRegistry.DetectAll) { }

    internal WaveInterfaces(DeviceManager primary, ILogger<DeviceManager> deviceLog, IConfiguration configuration, Func<IReadOnlyList<IAudioDevice>> detect)
    {
        _detect = detect;
        _primary = primary; _deviceLog = deviceLog; _configuration = configuration;
        _primary.ClaimingDevice += ReservePrimary;
        try
        {
            if (File.Exists(PathName))
            {
                using var file = File.OpenRead(PathName);
                if (file.Length > 32 * 1024) throw new IOException("Wave interface preferences exceed 32 KiB.");
                byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
                if (file.ReadByte() != -1) throw new IOException("Wave interface preferences changed while reading.");
                var ids = JsonSerializer.Deserialize<string[]>(bytes) ?? throw new JsonException("Wave interface preferences must be an array.");
                if (ids.Length > MaximumAdditional || ids.Any(id => !ValidId(id)) || ids.Distinct().Count() != ids.Length)
                    throw new JsonException("Invalid Wave interface preferences.");
                _enabled = ids.ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _error = ex.Message; }
    }

    internal static bool ValidId(string? id) => UsbLocation.IsInstanceId(id);

    private void ReservePrimary(string id)
    {
        DeviceManager? session;
        lock (_sessionsGate) { Volatile.Write(ref _reserved, id); _sessions.TryRemove(id, out session); }
        if (session is not null)
        {
            session.SuspendSession();
            _ = StopSessionAsync(session);
        }
    }

    private async Task StopSessionAsync(DeviceManager session)
    {
        try { await session.StopAsync(CancellationToken.None); }
        catch (Exception ex) { _deviceLog.LogWarning(ex, "Additional interface shutdown failed"); }
        finally { session.Dispose(); }
    }

    public IReadOnlyList<WaveInterfaceState> Snapshot()
    {
        var available = Volatile.Read(ref _available);
        string? primary = _primary.ActiveInfo?.InstanceId;
        HashSet<string> enabled;
        lock (_preferencesGate) enabled = [.. _enabled];
        var states = available.Select(device =>
        {
            string id = device.Info.InstanceId;
            bool active = id == primary;
            StateMessage? state = active ? _primary.Snapshot() : _sessions.TryGetValue(id, out var session) ? session.Snapshot() : null;
            return new WaveInterfaceState(id, device.Info.DisplayName + (device.Info.Location is { } location ? $" ({location.Port})" : ""),
                active, enabled.Contains(id), state?.Connected ?? false, device.Info.NodeNameFragment,
                device.Capabilities, state?.State, _error ?? _discoveryError ?? (active ? _primary.Warning : _sessions.GetValueOrDefault(id)?.Warning));
        }).ToList();
        // Keep remembered offline entries reachable so unplugged or moved
        // units cannot permanently consume all four enable slots in the UI.
        var attached = available.Select(device => device.Info.InstanceId).ToHashSet(StringComparer.Ordinal);
        states.AddRange(enabled.Where(id => !attached.Contains(id)).Order(StringComparer.Ordinal).Select(id =>
            new WaveInterfaceState(id, $"Unavailable Wave interface ({id})", false, true, false, "", new(), null,
                Warning ?? "This interface is not attached. Disable its additional role to forget it.")));
        return states;
    }

    public string? SetEnabled(string id, bool enabled)
    {
        if (!ValidId(id)) return "Invalid Wave interface ID.";
        if (_stopping) return "The daemon is stopping.";
        lock (_preferencesGate)
        {
            if (_error is not null) return "Repair Wave interface preferences before changing them: " + _error;
            if (enabled && !Volatile.Read(ref _available).Any(d => d.Info.InstanceId == id)) return "That Wave interface is not attached.";
            var attached = Volatile.Read(ref _available);
            var unit = attached.FirstOrDefault(d => d.Info.InstanceId == id);
            if (enabled && unit?.Capabilities.OutputRouting == true
                && attached.Count(d => string.Equals(d.Info.NodeNameFragment, unit.Info.NodeNameFragment, StringComparison.OrdinalIgnoreCase)) > 1)
                return "These interfaces have no unique audio-card identity. Choose a primary interface instead.";
            var next = new HashSet<string>(_enabled, StringComparer.Ordinal);
            if (enabled) next.Add(id); else next.Remove(id);
            if (next.Count > MaximumAdditional) return "At most four additional Wave interfaces can be enabled.";
            try { OpenXlrPaths.WriteAtomic(PathName, JsonSerializer.Serialize(next.Order(StringComparer.Ordinal))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
            _enabled = next;
        }
        if (!enabled && _sessions.TryRemove(id, out var session)) { session.SuspendSession(); _ = StopSessionAsync(session); }
        Changed?.Invoke();
        return null;
    }

    public string? Apply(string id, string control, JsonElement value)
    {
        if (!ValidId(id)) return "Invalid Wave interface ID.";
        // The primary reserves its instance before opening it. A stale secondary
        // command must not reopen that instance or write to a different microphone.
        if (id == Volatile.Read(ref _reserved)) return _primary.ActiveInfo?.InstanceId == id
            ? _primary.Apply(control, value) : "The primary interface is reconnecting.";
        if (!_sessions.TryGetValue(id, out var session)) return "Enable this attached Wave interface first.";
        return session.Apply(control, value);
    }

    public Dictionary<string, DeviceState> CaptureProfile()
    {
        var result = new Dictionary<string, DeviceState>(StringComparer.Ordinal);
        foreach (var pair in _sessions.ToArray())
            if (pair.Value.Snapshot().State is { } state) result[pair.Key] = DeviceStateStore.Hardware(state);
        return result;
    }

    public string? ApplyProfile(IReadOnlyDictionary<string, DeviceState> states)
    {
        foreach (var (id, state) in states)
        {
            // A profile never enables a device or redirects a state to a
            // different unit of the same model. Unavailable units stay alone.
            if (id == Volatile.Read(ref _reserved) || !_sessions.TryGetValue(id, out var session)) continue;
            if (session.CurrentConnection is not { } connection) continue;
            string? error = null;
            if (session.WithConnection(connection, () => { error = session.ApplyProfile(state, restoring: true); }))
            {
                if (error is not null) return $"{id}: {error}";
                session.MarkRestored();
            }
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                IReadOnlyList<IAudioDevice> devices;
                try { devices = _detect(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _discoveryError = "USB discovery failed: " + ex.Message;
                    Changed?.Invoke();
                    await Task.Delay(TimeSpan.FromSeconds(1), stop);
                    continue;
                }
                bool changed = _discoveryError is not null || !devices.Select(d => d.Info).SequenceEqual(Volatile.Read(ref _available).Select(d => d.Info));
                _discoveryError = null;
                Volatile.Write(ref _available, devices);
                HashSet<string> enabled;
                lock (_preferencesGate) enabled = [.. _enabled];
                string? reserved = Volatile.Read(ref _reserved) ?? _primary.ActiveInfo?.InstanceId;
                foreach (var pair in _sessions.ToArray())
                    if (pair.Key == reserved || !enabled.Contains(pair.Key) || !devices.Any(d => d.Info.InstanceId == pair.Key))
                    {
                        if (_sessions.TryRemove(pair.Key, out var old)) { old.SuspendSession(); await StopSessionAsync(old); }
                    }
                foreach (var device in devices.Where(d => d.Info.InstanceId != reserved && enabled.Contains(d.Info.InstanceId)))
                {
                    string id = device.Info.InstanceId;
                    if (_sessions.ContainsKey(id)) continue;
                    var session = new DeviceManager(_deviceLog, _configuration,
                        () => [.. Volatile.Read(ref _available).Where(d => d.Info.InstanceId == id && id != Volatile.Read(ref _reserved))]);
                    session.SetSessionStorageId(id);
                    session.StateChanged += _ => Changed?.Invoke();
                    session.DeviceArrived += arrived =>
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                if (session.CurrentConnection is { } connection)
                                    session.WithConnection(connection, () => { session.RestoreLastState(); session.MarkRestored(); });
                            }
                            catch (Exception ex) { _deviceLog.LogWarning(ex, "Restoring additional interface {id} failed", id); }
                        });
                    };
                    bool added;
                    Task? starting = null;
                    lock (_sessionsGate)
                    {
                        lock (_preferencesGate)
                        {
                            added = !_stopping && id != Volatile.Read(ref _reserved) && _enabled.Contains(id) && _sessions.TryAdd(id, session);
                            if (added) starting = session.StartAsync(stop);
                        }
                    }
                    if (added) { await starting!; changed = true; } else session.Dispose();
                }
                if (changed) Changed?.Invoke();
                await Task.Delay(TimeSpan.FromSeconds(1), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            lock (_sessionsGate) _stopping = true;
            _primary.ClaimingDevice -= ReservePrimary;
            foreach (var pair in _sessions.ToArray())
                if (_sessions.TryRemove(pair.Key, out var session)) { session.SuspendSession(); await StopSessionAsync(session); }
        }
    }
}
