using System.Text.Json;
using OpenXLR.Core;
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

    // Whether this run builds the submixer (same decision MixerService
    // makes). Only then does the card need the pro-audio profile; in
    // hardware-control mode the stock layout (UCM split) stays in place.
    private readonly bool _submixer;

    public DeviceManager(ILogger<DeviceManager> log, IConfiguration config)
    {
        _log = log;
        string? want = Environment.GetEnvironmentVariable("OPENXLR_DEVICE");
        if (want is not null && ushort.TryParse(want, System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
            _preferredPid = pid;
        bool launchDefault = config.GetValue("mixer", false) ||
                             Environment.GetEnvironmentVariable("OPENXLR_BUILD_MIXER") == "1";
        _submixer = OpenXLR.Core.DaemonSettings.SubmixerEnabled(launchDefault);
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
                RestoreCardProfile();   // the parked UCM split comes back with the device released
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

    /// <summary>Capabilities of the connected device, or null; cheap to poll.</summary>
    public DeviceCapabilities? ActiveCapabilities
    {
        get { lock (_gate) return _device is { Connected: true } ? _device.Capabilities : null; }
    }

    // Software gain lock (Wave Link's Gain Lock, app-side on these devices):
    // per-device usbIds persisted so the lock survives restarts. The daemon
    // stamps the flag onto every state snapshot and rejects gain writes while
    // set, so every client honors it without needing its own logic.
    private readonly HashSet<string> _gainLocked = LoadGainLocks();

    private static string GainLockPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "openxlr", "gainlock.json");

    private static HashSet<string> LoadGainLocks()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(GainLockPath)) ?? []; }
        catch (Exception) { return []; }
    }

    private void SaveGainLocks()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GainLockPath)!);
            File.WriteAllText(GainLockPath, JsonSerializer.Serialize(_gainLocked));
        }
        catch (Exception) { /* best effort */ }
    }

    private static string DevId(IAudioDevice d) => $"{d.Info.VendorId:x4}:{d.Info.ProductId:x4}";

    private bool GainIsLocked => _device is not null && _gainLocked.Contains(DevId(_device));

    // The Pro's firmware mutes an XLR input for ~13 s around every 48V
    // transition (anti-thump) and unmutes it itself, ignoring host unmutes
    // meanwhile. Stamp a settling flag for 15 s after each phantom write so
    // clients can present that hold instead of a stuck mute button.
    private static readonly TimeSpan PhantomSettleWindow = TimeSpan.FromSeconds(15);
    private DateTime _phantomWroteAt = DateTime.MinValue;
    private DateTime _phantomWroteAt2 = DateTime.MinValue;

    // Whole seconds left in a settle window (0 outside it). The 100 ms poll
    // notices each one-second step, so clients get a ticking countdown from
    // the ordinary state broadcasts.
    private int SettleSecondsLeft(DateTime wroteAt)
    {
        double left = (PhantomSettleWindow - (DateTime.UtcNow - wroteAt)).TotalSeconds;
        return left > 0 ? (int)Math.Ceiling(left) : 0;
    }

    // The hold ends when the firmware's own unmute shows up in the readback;
    // the window is only a cap (and the grace covers the moment right after
    // the write, before the firmware's mute is first observed).
    private static readonly TimeSpan PhantomMuteGrace = TimeSpan.FromSeconds(2);

    private DeviceState Stamp(DeviceState s)
    {
        DateTime now = DateTime.UtcNow;
        bool held1 = SettleSecondsLeft(_phantomWroteAt) > 0
                     && (s.Mute || now - _phantomWroteAt < PhantomMuteGrace);
        bool held2 = SettleSecondsLeft(_phantomWroteAt2) > 0
                     && (s.Mute2 || now - _phantomWroteAt2 < PhantomMuteGrace);
        return s with
        {
            GainLocked = GainIsLocked,
            PhantomSettling = held1,
            PhantomSettling2 = held2,
            PhantomSettleSeconds = held1 ? SettleSecondsLeft(_phantomWroteAt) : 0,
            PhantomSettleSeconds2 = held2 ? SettleSecondsLeft(_phantomWroteAt2) : 0,
        };
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
                State = _last ?? Stamp(_device.ReadState()),
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
                TryParkCardProfile();
            }
            catch (UsbHungException ex)
            {
                NoteHung(ex);
            }
            catch (Exception ex)
            {
                _log.LogWarning("device loop: {msg}", ex.Message);
                Drop();
            }
            await Task.Delay(100, stop).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        Drop();
        RestoreCardProfile();
    }

    // After a transfer that never returned, the worker thread is still parked
    // in libusb on the old handle. Reconnecting straight away would hang the
    // same way and leak a thread every few seconds, so wait before retrying.
    private static readonly TimeSpan HungReconnectDelay = TimeSpan.FromSeconds(10);
    private DateTime _reconnectNotBefore = DateTime.MinValue;

    /// <summary>
    /// The last transfer that never returned, kept for the diagnostics
    /// archive (Options, SUPPORT, Collect diagnostics) so a tester can hand
    /// over the exact setup packet, payload, timing and versions without
    /// digging through the journal.
    /// </summary>
    private string? _lastUsbFault;

    private void NoteHung(UsbHungException ex)
    {
        string device = _device is null ? "no device"
            : $"{_device.Info.DisplayName} {_device.Info.VendorId:x4}:{_device.Info.ProductId:x4}";
        _lastUsbFault = $"{DateTime.UtcNow:O} {device}, kernel {KernelRelease()}: {ex.Message}";
        _log.LogError("{fault}. The device is dropped and reconnected in {s} s. " +
                      "Please collect diagnostics (Options, SUPPORT) and attach the archive to an issue.",
            _lastUsbFault, (int)HungReconnectDelay.TotalSeconds);
        _reconnectNotBefore = DateTime.UtcNow + HungReconnectDelay;
        Drop();
    }

    private static string KernelRelease()
    {
        try { return File.ReadAllText("/proc/sys/kernel/osrelease").Trim(); }
        catch (Exception) { return "unknown"; }
    }

    private void EnsureConnected()
    {
        lock (_gate)
        {
            IReadOnlyList<IAudioDevice> all = DeviceRegistry.DetectAll();
            _detected = [.. all.Select(d => d.Info)];
            if (_device is { Connected: true }) return;
            if (DateTime.UtcNow < _reconnectNotBefore) return;
            IAudioDevice? dev = _preferredPid is ushort pid
                ? all.FirstOrDefault(d => d.Info.ProductId == pid) ?? (all.Count > 0 ? all[0] : null)
                : all.Count > 0 ? all[0] : null;
            if (dev is null) return;                    // nothing attached; try again next tick
            dev.Connect();
            _device = dev;
            _last = null;
            _log.LogInformation("connected {dev}", dev.Info.DisplayName);
            EnsureCardProfile(dev.Info);
            RaiseFromLocked();                          // push the initial state
        }
    }

    // UCM coexistence: a card with a UCM split profile hides the raw
    // multichannel nodes the mixer links against, so drive it in pro-audio
    // while connected and restore the split on graceful shutdown.
    private string? _profileRestore;
    private string? _profileCardFragment;
    private int _profileChecksLeft;
    private DateTime _profileNextCheck;

    private void EnsureCardProfile(DeviceInfo info)
    {
        if (!_submixer) return;   // hardware-control mode leaves the card's layout alone
        // The USB device is visible to libusb before WirePlumber has created
        // (and profiled) the card, so a single check at connect misses the
        // boot case entirely: keep checking until the card settles.
        _profileCardFragment = info.Model.Replace(' ', '_');
        _profileChecksLeft = 60;
        _profileNextCheck = DateTime.UtcNow;
        TryParkCardProfile();
    }

    private void TryParkCardProfile()
    {
        if (_profileChecksLeft <= 0 || DateTime.UtcNow < _profileNextCheck) return;
        string? fragment = _profileCardFragment;
        if (fragment is null) { _profileChecksLeft = 0; return; }
        try
        {
            (string? active, string? parked) = OpenXLR.Core.Mixing.CardProfile.EnsureProAudio(fragment);
            if (parked is not null)
            {
                _profileRestore = parked;
                _log.LogInformation("card {frag}: UCM profile {prev} parked, pro-audio active", fragment, parked);
            }
            // Settled: we parked it, or it already runs pro-audio. Anything
            // else (card absent, or still "off" while the session manager
            // brings it up) gets another look.
            if (parked is not null || active == "pro-audio") { _profileChecksLeft = 0; return; }
        }
        catch (Exception ex)
        {
            _log.LogWarning("card profile check: {msg}", ex.Message);
        }
        _profileChecksLeft--;
        _profileNextCheck = DateTime.UtcNow.AddSeconds(2);
        if (_profileChecksLeft == 0)
            _log.LogWarning("card {frag}: gave up waiting for a pro-audio profile", fragment);
    }

    // Restore at the START of shutdown: the teardown of the rest of the
    // daemon can take many seconds, and the profile flip must not depend
    // on it completing. RestoreCardProfile is idempotent, so the post-loop
    // call in ExecuteAsync stays as a fallback for non-host exits.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        RestoreCardProfile();
        await base.StopAsync(cancellationToken);
    }

    private void RestoreCardProfile()
    {
        _profileChecksLeft = 0;
        if (_profileRestore is null || _profileCardFragment is null) return;
        try
        {
            OpenXLR.Core.Mixing.CardProfile.SetProfile(_profileCardFragment, _profileRestore);
            _log.LogInformation("card {frag}: restored UCM profile {prev}", _profileCardFragment, _profileRestore);
        }
        catch (Exception ex)
        {
            _log.LogWarning("card profile restore: {msg}", ex.Message);
        }
        _profileRestore = null;
        _profileCardFragment = null;
    }

    private void PollOnce()
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return;
            DeviceState now = Stamp(_device.ReadState());
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
                    case ControlNames.Gain:
                        if (GainIsLocked) return "gain is locked";
                        _device.SetGainDb(value.GetInt32());
                        break;
                    case ControlNames.Mute: _device.SetMute(value.GetBoolean()); break;
                    case ControlNames.LowCut: _device.SetLowCut(value.GetBoolean()); break;
                    case ControlNames.Expander: _device.SetExpander(value.GetBoolean()); break;
                    case ControlNames.VoiceTune: _device.SetVoiceTune(value.GetBoolean()); break;
                    case ControlNames.VoiceTuneStrength: _device.SetVoiceTuneStrength(value.GetInt32()); break;
                    case ControlNames.HpVolumeDb: _device.SetHpVolumeDb(value.GetDouble()); break;
                    case ControlNames.LowImpedance: _device.SetLowImpedance(value.GetBoolean()); break;
                    case ControlNames.Crossfade: _device.SetCrossfade(value.GetInt32()); break;
                    case ControlNames.Phantom:
                        _device.SetPhantom(value.GetBoolean());
                        _phantomWroteAt = DateTime.UtcNow;
                        break;
                    case ControlNames.ClipGuard: _device.SetClipGuard(value.GetBoolean()); break;
                    case ControlNames.Compressor: _device.SetCompressor(value.GetBoolean()); break;
                    case ControlNames.OutHp1: _device.SetOutHp1(value.GetBoolean()); break;
                    case ControlNames.OutHp2: _device.SetOutHp2(value.GetBoolean()); break;
                    case ControlNames.OutUsbAux: _device.SetOutUsbAux(value.GetBoolean()); break;
                    case ControlNames.OutLineOut: _device.SetOutLineOut(value.GetBoolean()); break;
                    case ControlNames.AuxLevelDb: _device.SetAuxLevelDb(value.GetDouble()); break;
                    case ControlNames.AuxLevelLock: _device.SetAuxLevelLock(value.GetBoolean()); break;
                    case "hp2VolumeDb": _device.SetHp2VolumeDb(value.GetDouble()); break;
                    case "gain2":
                        if (GainIsLocked) return "gain is locked";
                        _device.SetGain2Db(value.GetInt32());
                        break;
                    case "gainLock":
                        if (value.GetBoolean()) _gainLocked.Add(DevId(_device));
                        else _gainLocked.Remove(DevId(_device));
                        SaveGainLocks();
                        break;
                    case "mute2": _device.SetMute2(value.GetBoolean()); break;
                    case "lowCut2": _device.SetLowCut2(value.GetBoolean()); break;
                    case "expander2": _device.SetExpander2(value.GetBoolean()); break;
                    case "voiceTune2": _device.SetVoiceTune2(value.GetBoolean()); break;
                    case "voiceTuneStrength2": _device.SetVoiceTuneStrength2(value.GetInt32()); break;
                    case "phantom2":
                        _device.SetPhantom2(value.GetBoolean());
                        _phantomWroteAt2 = DateTime.UtcNow;
                        break;
                    case "clipGuard2": _device.SetClipGuard2(value.GetBoolean()); break;
                    case "compressor2": _device.SetCompressor2(value.GetBoolean()); break;
                    default: return $"unknown control '{control}'";
                }
            }
            catch (UsbHungException ex)
            {
                NoteHung(ex);
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            // Reflect immediately; the poll loop will also catch it, but this
            // makes the client's own change feel instant.
            _last = Stamp(_device.ReadState());
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
            DeviceState s = _last ?? Stamp(_device.ReadState());
            try
            {
                if (s.GainDb != p.GainDb) _device.SetGainDb(p.GainDb);
                if (s.Mute != p.Mute) _device.SetMute(p.Mute);
                if (s.LowCut != p.LowCut) _device.SetLowCut(p.LowCut);
                if (s.Expander != p.Expander) _device.SetExpander(p.Expander);
                if (s.VoiceTune != p.VoiceTune) _device.SetVoiceTune(p.VoiceTune);
                if (s.VoiceTuneStrength != p.VoiceTuneStrength) _device.SetVoiceTuneStrength(p.VoiceTuneStrength);
                if (s.Phantom != p.Phantom) { _device.SetPhantom(p.Phantom); _phantomWroteAt = DateTime.UtcNow; }
                if (s.ClipGuard != p.ClipGuard) _device.SetClipGuard(p.ClipGuard);
                if (s.Compressor != p.Compressor) _device.SetCompressor(p.Compressor);
                if (s.Gain2Db != p.Gain2Db) _device.SetGain2Db(p.Gain2Db);
                if (s.Mute2 != p.Mute2) _device.SetMute2(p.Mute2);
                if (s.LowCut2 != p.LowCut2) _device.SetLowCut2(p.LowCut2);
                if (s.Expander2 != p.Expander2) _device.SetExpander2(p.Expander2);
                if (s.VoiceTune2 != p.VoiceTune2) _device.SetVoiceTune2(p.VoiceTune2);
                if (s.VoiceTuneStrength2 != p.VoiceTuneStrength2) _device.SetVoiceTuneStrength2(p.VoiceTuneStrength2);
                if (s.Phantom2 != p.Phantom2) { _device.SetPhantom2(p.Phantom2); _phantomWroteAt2 = DateTime.UtcNow; }
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
            _last = Stamp(_device.ReadState());
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
                    _last = Stamp(_device.ReadState());
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
            var blocks = new Dictionary<string, string>();
            if (_lastUsbFault is not null) blocks["usbFault"] = _lastUsbFault;
            if (_device is null || !_device.Connected) return blocks;
            try
            {
                foreach ((string k, string v) in _device.DumpBlocks()) blocks[k] = v;
            }
            catch (UsbHungException ex) { NoteHung(ex); blocks["error"] = ex.Message; }
            catch (Exception ex) { blocks["error"] = ex.Message; }
            return blocks;
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
