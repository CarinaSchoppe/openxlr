using System.Text.Json;
using OpenXLR.Core.Devices;

namespace OpenXLR.Daemon;

/// <summary>
/// Owns the connected audio interface. A background loop connects (and
/// reconnects) the first supported device, polls its state, and raises
/// <see cref="StateChanged"/> when anything moves, including changes made by
/// other clients, so every UI/plugin stays in sync. Commands from clients are
/// applied here; the device's own lock serialises the USB transfers.
/// </summary>
public sealed class DeviceManager : BackgroundService
{
    private readonly ILogger<DeviceManager> _log;
    private readonly object _gate = new();
    private IAudioDevice? _device;
    private DeviceState? _last;
    private IReadOnlyList<DeviceInfo> _detected = [];
    private ushort? _preferredPid;

    public DeviceManager(ILogger<DeviceManager> log)
    {
        _log = log;
        string? want = Environment.GetEnvironmentVariable("OPENXLR_DEVICE");
        if (want is not null && ushort.TryParse(want, System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
            _preferredPid = pid;
    }

    /// <summary>Every supported interface currently attached, for client pickers.</summary>
    public IReadOnlyList<(string UsbId, string Name, bool Active)> Detected()
    {
        lock (_gate)
        {
            return [.. _detected.Select(d => (
                $"{d.VendorId:x4}:{d.ProductId:x4}",
                d.DisplayName,
                _device is { Connected: true } && _device.Info.ProductId == d.ProductId))];
        }
    }

    /// <summary>
    /// Switch to another attached supported device ("vvvv:pppp" or "pppp").
    /// The connect loop picks it up within a poll tick. Null on success.
    /// </summary>
    public string? SetActiveDevice(string usbId)
    {
        string pidPart = usbId.Contains(':') ? usbId.Split(':')[1] : usbId;
        if (!ushort.TryParse(pidPart, System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
            return $"setActiveDevice: bad device id '{usbId}'";
        lock (_gate)
        {
            if (!_detected.Any(d => d.ProductId == pid))
                return $"setActiveDevice: no attached supported device '{usbId}'";
            _preferredPid = pid;
            if (_device is not null && _device.Info.ProductId != pid)
            {
                try { _device.Disconnect(); } catch { /* releasing anyway */ }
                _device = null;
                _last = null;
                RaiseFromLocked();   // show the handoff instead of stale state
            }
        }
        return null;
    }

    /// <summary>Identity of the connected device, or null; cheap to poll.</summary>
    public DeviceInfo? ActiveInfo
    {
        get { lock (_gate) return _device is { Connected: true } ? _device.Info : null; }
    }

    /// <summary>Raised (off the poll loop) whenever the pushed state should change.</summary>
    public event Action<StateMessage>? StateChanged;

    public StateMessage Snapshot()
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected)
                return new StateMessage { Connected = false };
            return new StateMessage
            {
                Connected = true,
                Device = new DeviceDescriptor(_device.Info.Vendor, _device.Info.Model,
                    $"{_device.Info.VendorId:x4}:{_device.Info.ProductId:x4}"),
                Capabilities = _device.Capabilities,
                State = _last ?? _device.ReadState(),
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                EnsureConnected();
                PollOnce();
            }
            catch (Exception ex)
            {
                _log.LogWarning("device loop: {msg}", ex.Message);
                Drop();
            }
            await Task.Delay(100, stop).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        Drop();
    }

    private void EnsureConnected()
    {
        lock (_gate)
        {
            IReadOnlyList<IAudioDevice> all = DeviceRegistry.DetectAll();
            _detected = [.. all.Select(d => d.Info)];
            if (_device is { Connected: true }) return;
            IAudioDevice? dev = _preferredPid is ushort pid
                ? all.FirstOrDefault(d => d.Info.ProductId == pid) ?? (all.Count > 0 ? all[0] : null)
                : all.Count > 0 ? all[0] : null;
            if (dev is null) return;                    // nothing attached; try again next tick
            dev.Connect();
            _device = dev;
            _last = null;
            _log.LogInformation("connected {dev}", dev.Info.DisplayName);
            RaiseFromLocked();                          // push the initial state
        }
    }

    private void PollOnce()
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return;
            DeviceState now = _device.ReadState();
            if (_last is null || now != _last)
            {
                _last = now;
                RaiseFromLocked();
            }
        }
    }

    /// <summary>Apply a client "set" command. Returns null on success, else an error string.</summary>
    public string? Apply(string control, JsonElement value)
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return "no device connected";
            try
            {
                switch (control)
                {
                    case ControlNames.Gain: _device.SetGainDb(value.GetInt32()); break;
                    case ControlNames.Mute: _device.SetMute(value.GetBoolean()); break;
                    case ControlNames.LowCut: _device.SetLowCut(value.GetBoolean()); break;
                    case ControlNames.Expander: _device.SetExpander(value.GetBoolean()); break;
                    case ControlNames.VoiceTune: _device.SetVoiceTune(value.GetBoolean()); break;
                    case ControlNames.VoiceTuneStrength: _device.SetVoiceTuneStrength(value.GetInt32()); break;
                    case ControlNames.HpVolumeDb: _device.SetHpVolumeDb(value.GetDouble()); break;
                    case ControlNames.LowImpedance: _device.SetLowImpedance(value.GetBoolean()); break;
                    case ControlNames.Crossfade: _device.SetCrossfade(value.GetInt32()); break;
                    case ControlNames.Phantom: _device.SetPhantom(value.GetBoolean()); break;
                    case ControlNames.ClipGuard: _device.SetClipGuard(value.GetBoolean()); break;
                    case ControlNames.Compressor: _device.SetCompressor(value.GetBoolean()); break;
                    case ControlNames.OutHp1: _device.SetOutHp1(value.GetBoolean()); break;
                    case ControlNames.OutHp2: _device.SetOutHp2(value.GetBoolean()); break;
                    case ControlNames.OutUsbAux: _device.SetOutUsbAux(value.GetBoolean()); break;
                    case ControlNames.OutLineOut: _device.SetOutLineOut(value.GetBoolean()); break;
                    case ControlNames.AuxLevelDb: _device.SetAuxLevelDb(value.GetDouble()); break;
                    case ControlNames.AuxLevelLock: _device.SetAuxLevelLock(value.GetBoolean()); break;
                    case "hp2VolumeDb": _device.SetHp2VolumeDb(value.GetDouble()); break;
                    case "gain2": _device.SetGain2Db(value.GetInt32()); break;
                    case "mute2": _device.SetMute2(value.GetBoolean()); break;
                    case "lowCut2": _device.SetLowCut2(value.GetBoolean()); break;
                    case "expander2": _device.SetExpander2(value.GetBoolean()); break;
                    case "voiceTune2": _device.SetVoiceTune2(value.GetBoolean()); break;
                    case "voiceTuneStrength2": _device.SetVoiceTuneStrength2(value.GetInt32()); break;
                    case "phantom2": _device.SetPhantom2(value.GetBoolean()); break;
                    case "clipGuard2": _device.SetClipGuard2(value.GetBoolean()); break;
                    case "compressor2": _device.SetCompressor2(value.GetBoolean()); break;
                    default: return $"unknown control '{control}'";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            // Reflect immediately; the poll loop will also catch it, but this
            // makes the client's own change feel instant.
            _last = _device.ReadState();
            RaiseFromLocked();
            return null;
        }
    }

    /// <summary>
    /// Recall a profile's hardware snapshot, writing only the fields that
    /// differ from the live state. The physical-output selectors are skipped
    /// on purpose: they follow the mixer's monitor-output selection (synced
    /// every sweep), which the profile's mixer half restores instead.
    /// </summary>
    public string? ApplyProfile(DeviceState p)
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return "no device connected";
            DeviceState s = _last ?? _device.ReadState();
            try
            {
                if (s.GainDb != p.GainDb) _device.SetGainDb(p.GainDb);
                if (s.Mute != p.Mute) _device.SetMute(p.Mute);
                if (s.LowCut != p.LowCut) _device.SetLowCut(p.LowCut);
                if (s.Expander != p.Expander) _device.SetExpander(p.Expander);
                if (s.VoiceTune != p.VoiceTune) _device.SetVoiceTune(p.VoiceTune);
                if (s.VoiceTuneStrength != p.VoiceTuneStrength) _device.SetVoiceTuneStrength(p.VoiceTuneStrength);
                if (s.Phantom != p.Phantom) _device.SetPhantom(p.Phantom);
                if (s.ClipGuard != p.ClipGuard) _device.SetClipGuard(p.ClipGuard);
                if (s.Compressor != p.Compressor) _device.SetCompressor(p.Compressor);
                if (s.Gain2Db != p.Gain2Db) _device.SetGain2Db(p.Gain2Db);
                if (s.Mute2 != p.Mute2) _device.SetMute2(p.Mute2);
                if (s.LowCut2 != p.LowCut2) _device.SetLowCut2(p.LowCut2);
                if (s.Expander2 != p.Expander2) _device.SetExpander2(p.Expander2);
                if (s.VoiceTune2 != p.VoiceTune2) _device.SetVoiceTune2(p.VoiceTune2);
                if (s.VoiceTuneStrength2 != p.VoiceTuneStrength2) _device.SetVoiceTuneStrength2(p.VoiceTuneStrength2);
                if (s.Phantom2 != p.Phantom2) _device.SetPhantom2(p.Phantom2);
                if (s.ClipGuard2 != p.ClipGuard2) _device.SetClipGuard2(p.ClipGuard2);
                if (s.Compressor2 != p.Compressor2) _device.SetCompressor2(p.Compressor2);
                if (s.HpVolumeDb != p.HpVolumeDb) _device.SetHpVolumeDb(p.HpVolumeDb);
                if (s.Hp2VolumeDb != p.Hp2VolumeDb) _device.SetHp2VolumeDb(p.Hp2VolumeDb);
                if (s.LowImpedance != p.LowImpedance) _device.SetLowImpedance(p.LowImpedance);
                if (s.Crossfade != p.Crossfade) _device.SetCrossfade(p.Crossfade);
                if (s.AuxLevelDb != p.AuxLevelDb) _device.SetAuxLevelDb(p.AuxLevelDb);
                if (s.AuxLevelLock != p.AuxLevelLock) _device.SetAuxLevelLock(p.AuxLevelLock);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            _last = _device.ReadState();
            RaiseFromLocked();
            return null;
        }
    }

    /// <summary>
    /// Drive the device's physical-output selectors toward the wanted state,
    /// writing only the ones that differ (called every mixer sweep, so it must
    /// be a no-op at steady state). Quietly does nothing without a connected
    /// device that has output routing.
    /// </summary>
    public void EnsureOutputSelectors(bool hp1, bool hp2, bool usbAux, bool lineOut)
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected || !_device.Capabilities.OutputRouting) return;
            DeviceState? s = _last;
            if (s is null) return;
            try
            {
                bool changed = false;
                if (s.OutHp1 != hp1) { _device.SetOutHp1(hp1); changed = true; }
                if (s.OutHp2 != hp2) { _device.SetOutHp2(hp2); changed = true; }
                // The aux output needs its matrix cell open besides the
                // selector, so re-apply when either is missing.
                if (s.OutUsbAux != usbAux || (usbAux && !s.AuxReturnEnabled))
                { _device.SetOutUsbAux(usbAux); changed = true; }
                if (s.OutLineOut != lineOut) { _device.SetOutLineOut(lineOut); changed = true; }
                if (changed)
                {
                    _last = _device.ReadState();
                    RaiseFromLocked();
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug("output selector sync: {msg}", ex.Message);
            }
        }
    }

    /// <summary>Vendor block dumps for diagnostics; empty without a device.</summary>
    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return new Dictionary<string, string>();
            try { return _device.DumpBlocks(); }
            catch (Exception ex) { return new Dictionary<string, string> { ["error"] = ex.Message }; }
        }
    }

    private void RaiseFromLocked()
    {
        StateMessage msg = _device is { Connected: true }
            ? new StateMessage
            {
                Connected = true,
                Device = new DeviceDescriptor(_device.Info.Vendor, _device.Info.Model,
                    $"{_device.Info.VendorId:x4}:{_device.Info.ProductId:x4}"),
                Capabilities = _device.Capabilities,
                State = _last,
            }
            : new StateMessage { Connected = false };
        // Fire outside the caller's expectations but we're already under _gate;
        // handlers only enqueue/serialize, they don't call back into the manager.
        StateChanged?.Invoke(msg);
    }

    private void Drop()
    {
        lock (_gate)
        {
            try { _device?.Disconnect(); } catch { /* ignore */ }
            bool was = _device is not null;
            _device = null;
            _last = null;
            if (was) StateChanged?.Invoke(new StateMessage { Connected = false });
        }
    }
}
