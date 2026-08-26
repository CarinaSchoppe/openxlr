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

    public DeviceManager(ILogger<DeviceManager> log) => _log = log;

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
            if (_device is { Connected: true }) return;
            IAudioDevice? dev = DeviceRegistry.DetectFirst();
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
