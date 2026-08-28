namespace OpenXLR.Core.Mixing;

/// <summary>
/// Builds and maintains the submix graph, entirely from PipeWire filter sinks so
/// every node is clocked by construction and audio always flows:
///
///   application -> channel (combine sink over all mixes) -> mixes (null
///   sinks) -> direct port links -> the chosen output device.
///
/// A combine sink runs one internal stream per mix it feeds, and each of those
/// streams has its own volume and mute: those streams ARE the faders, so the
/// whole matrix needs only 7 channel sinks and 3 mix sinks. Everything is
/// clocked through the output device via the direct links (an earlier
/// loopback-based design stalled because its islands had no clock driver, and
/// a remap-cell design worked but exposed 21 extra sinks, which overwhelmed
/// desktop applets and helped exhaust pipewire-pulse's file descriptors).
///
/// The graph is built once; level changes touch only stream volumes, so audio
/// is never interrupted.
/// </summary>
public sealed class Mixer : IDisposable
{
    private readonly PipeWireAdapter _pw;
    private readonly Dictionary<string, double> _levels = [];   // "channel|mix" -> level
    private readonly HashSet<string> _muted = [];               // "channel|mix"
    private readonly HashSet<string> _cells = [];               // cells that exist
    private readonly Dictionary<string, double> _mixVolume = [];
    private readonly HashSet<string> _mixMuted = [];
    private readonly object _gate = new();

    // The monitor mix's routes to its output devices (direct port links; the
    // monitor can feed several at once) and the hardware interface's per-pair
    // feeds into the input channels (XLR 1, XLR 2, Line In). Routes are keyed
    // by physical link target, so several pseudo-outputs sharing one underlying
    // route (the Wave XLR Pro's jacks all ride its monitor bus) make one link.
    private readonly Dictionary<string, PortLink> _monitorRoutes = [];
    private readonly List<string> _monitorOutputs = [];
    private readonly List<PortLink> _inputFeeds = [];
    private string? _inputDevice;   // the capture device the feeds come from

    // The Aux mix's route into the device's USB Aux port (return pair), and
    // whether the user wants that port fed at all.
    private PortLink? _auxRoute;
    private string? _auxTargetSink;
    private bool _auxPortEnabled = true;

    // Cached hardware volume of the selected output device, so external
    // changes (KDE applet, hardware knobs) can be detected and pushed.
    private double? _outputVolume;

    // Enforced system defaults (null = not enforced).
    private string? _enforcedSink;
    private string? _enforcedSource;

    private MixerConfig _config = MixerConfig.Default();
    private bool _built;

    private MeterReader _meters = new();

    public Mixer(PipeWireAdapter? adapter = null) => _pw = adapter ?? new PipeWireAdapter();

    /// <summary>Live stereo levels per channel and mix, keyed by id, as [L, R].</summary>
    public IReadOnlyDictionary<string, double[]> ReadMeters() => _meters.Read();

    public MixerConfig Config => _config;
    public bool Built => _built;

    private static string Cell(string channel, string mix) => $"{channel}|{mix}";

    // channel id -> its combine module; "channel|mix" -> that leg's sink-input index
    private readonly Dictionary<string, uint> _combineModules = [];
    private readonly Dictionary<string, int> _legIndex = [];

    /// <summary>Map every combine's internal streams to their (channel, mix) cells.</summary>
    private void DiscoverLegsLocked()
    {
        _legIndex.Clear();
        foreach ((string chId, uint module) in _combineModules)
        {
            IReadOnlyDictionary<string, int> legs = _pw.FindCombineLegs(module);
            foreach (MixDefinition mix in _config.Mixes)
                if (legs.TryGetValue(mix.SinkName, out int idx))
                    _legIndex[Cell(chId, mix.Id)] = idx;
        }
    }

    /// <summary>
    /// Create the whole graph. <paramref name="monitorOutputSink"/> is the sink
    /// the monitor mix feeds; <paramref name="defaultSource"/> is the capture
    /// device applications should record from by default. Both are optional and
    /// changeable at runtime, and neither affects whether audio flows.
    /// </summary>
    public void Build(MixerConfig config, string? monitorOutputSink = null, string? defaultSource = null)
    {
        lock (_gate)
        {
            if (_built) TearDownLocked();
            _config = config;

            // A crashed or killed daemon never runs its teardown, and loading
            // over its leftover nodes fails the whole build with a name
            // collision. Clear any stray OpenXLR modules first.
            _pw.UnloadStaleModules("OpenXLR_");

            // WirePlumber auto-switches the default capture device to newly
            // created sources (our virtual mics). Remember the user's current
            // one so it can be put back unless a choice was passed in.
            string? previousDefaultSource = _pw.GetDefaultSource();

            // Mixes first: the cells attach to them as masters.
            foreach (MixDefinition mix in config.Mixes)
            {
                _pw.CreateNullSink(mix.SinkName, $"OpenXLR {mix.Name}");
                _mixVolume[mix.Id] = mix.Volume;
                if (mix.Muted) _mixMuted.Add(mix.Id);
            }

            // One combine per channel, feeding every mix. Its internal streams
            // (one per mix) are the faders.
            foreach (ChannelDefinition ch in config.Channels)
            {
                foreach (MixDefinition mix in config.Mixes)
                {
                    string cell = Cell(ch.Id, mix.Id);
                    _levels[cell] = ch.Levels.TryGetValue(mix.Id, out double v) ? v : 0.0;
                    if (ch.MutedIn.Contains(mix.Id)) _muted.Add(cell);
                    _cells.Add(cell);
                }
                _combineModules[ch.Id] = _pw.CreateCombineSink(ch.SinkName,
                    config.Mixes.Select(m => m.SinkName),
                    $"OpenXLR {ch.Name}");
            }
            DiscoverLegsLocked();

            // Push initial fader values.
            foreach (MixDefinition mix in config.Mixes) ReapplyMixLocked(mix.Id);

            // Publish non-monitor mixes as selectable capture devices.
            foreach (MixDefinition mix in config.Mixes.Where(m => m.Kind == MixKind.VirtualMic))
                _pw.CreateVirtualMic(mix.VirtualMicName, $"{mix.SinkName}.monitor", $"OpenXLR {mix.Name}");

            // Meter every channel and mix so the UI can show what is flowing.
            foreach (ChannelDefinition ch in config.Channels) _meters.Add($"ch:{ch.Id}", ch.SinkName);
            foreach (MixDefinition mix in config.Mixes) _meters.Add($"mix:{mix.Id}", mix.SinkName);

            _built = true;

            if (monitorOutputSink is not null) SetMonitorOutputsLocked([monitorOutputSink]);
            _ = previousDefaultSource;   // defaults are governed by enforcement only
            _ = defaultSource;           // input channels are hardware-wired, not selectable
            WireInputFeedsLocked();
            WireAuxRouteLocked();
        }
    }

    /// <summary>
    /// Wire the hardware interface's capture pairs into their input channels
    /// (XLR 1 = pair 0, XLR 2 = pair 1, Line In = pair 2). The interface is
    /// found by name; when absent the input channels are silent and everything
    /// else still works. Safe to call again after a hotplug.
    /// </summary>
    private void WireInputFeedsLocked()
    {
        foreach (PortLink feed in _inputFeeds) _pw.Unlink(feed);
        _inputFeeds.Clear();
        RemoveLowCutLocked();
        var sources = _pw.ListDevices()
            .Where(d => d.Kind == AudioNodeKind.Source && !d.IsOwn).ToList();
        // Prefer the interface the daemon actively drives (the hint), so a
        // device switch moves the channel feeds with it; fall back to any
        // Wave XLR so the mixer still works when no device is connected.
        string? previousInput = _inputDevice;
        _inputDevice = (_inputHint is null ? null : sources.FirstOrDefault(
                d => d.Name.Contains(_inputHint, StringComparison.OrdinalIgnoreCase))?.Name)
            ?? sources.FirstOrDefault(
                d => d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))?.Name;
        if (_inputDevice is null) return;

        // Console rule: a newly patched input comes up muted. Switching the
        // feed device once put a hot mic straight into the monitor outputs
        // (a feedback howl through the speakers), so the hardware channels'
        // monitor sends start muted after a device change and the user
        // unmutes deliberately.
        if (previousInput is not null && previousInput != _inputDevice)
        {
            MixDefinition? mon = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
            if (mon is not null)
                foreach (ChannelDefinition hw in _config.Channels.Where(c => c.InputPair is not null))
                {
                    _muted.Add(Cell(hw.Id, mon.Id));
                    ApplyCellLocked(hw.Id, mon.Id);
                }
        }
        foreach (ChannelDefinition ch in _config.Channels.Where(c => c.InputPair is not null))
        {
            bool lc = _lowCutHz > 0 && _lowCutApplicable;
            bool cg = _softClipGuard && _clipGuardApplicable;
            if (ch.InputPair == 0 && (lc || cg))
            {
                // Route the mic through the filter chain instead of straight in.
                _lowCut = _pw.CreateMicFilter("xlr1", lc ? _lowCutHz : 0, cg);
                _pw.UnlinkNodes(_inputDevice, ch.SinkName);   // clear any stale bypass
                PortLink into = _pw.RouteInputToChannel(_inputDevice, _lowCut.SinkName, 0);
                if (into.Pairs.Count > 0) _inputFeeds.Add(into);
                _lowCutOut = _pw.LinkNodes(_lowCut.SourceName, "capture", ch.SinkName, "playback");
                continue;
            }
            PortLink feed = _pw.RouteInputToChannel(_inputDevice, ch.SinkName, ch.InputPair!.Value);
            if (feed.Pairs.Count > 0) _inputFeeds.Add(feed);
        }
    }

    private void RemoveLowCutLocked()
    {
        if (_lowCutOut is not null) { _pw.Unlink(_lowCutOut); _lowCutOut = null; }
        if (_lowCut is not null) { _pw.StopFilter(_lowCut); _lowCut = null; }
    }

    /// <summary>
    /// Sweep healing for the software low cut: a dead holder process or a
    /// broken link re-wires the whole input path. True when something changed.
    /// </summary>
    public bool EnsureLowCutRoutes()
    {
        lock (_gate)
        {
            if (!_built || _lowCut is null) return false;
            bool broken = _lowCut.Process.HasExited
                || (_lowCutOut is not null && _pw.EnsureLinks(_lowCutOut) == LinkHealth.Broken);
            if (!broken) return false;
            WireInputFeedsLocked();
            return true;
        }
    }

    /// <summary>
    /// Route the Aux mix into the device's USB Aux port (its dedicated return
    /// pair) when enabled and the device is present. The port selector and
    /// matrix cell are the daemon's job; this is only the PipeWire leg.
    /// </summary>
    private void WireAuxRouteLocked()
    {
        if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        _auxTargetSink = null;
        if (!_auxPortEnabled) return;
        MixDefinition? aux = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.AuxPort);
        if (aux is null) return;
        // The raw sink is hidden from pickers, so derive it from any Pro
        // pseudo-output's bare name.
        string? proSink = _pw.ListDevices()
            .Where(d => d.Kind == AudioNodeKind.Sink &&
                        d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))
            .Select(d => { int m = d.Name.IndexOf('#'); return m < 0 ? d.Name : d.Name[..m]; })
            .FirstOrDefault();
        if (proSink is null) return;
        PortLink route = _pw.RouteMixToOutput(aux.SinkName, proSink + "#usbaux");
        if (route.Pairs.Count > 0) { _auxRoute = route; _auxTargetSink = proSink; }
    }

    /// <summary>
    /// Bounce the aux port's physical sink so the device re-latches its return
    /// routing. A plain suspend cycle is not enough while our port links keep
    /// the sink busy, so every link to it is dropped first, the stream is
    /// cycled on a genuinely idle sink, and the links are rebuilt (which
    /// reopens the stream with the matrix already set).
    /// </summary>
    public void BounceAuxTarget()
    {
        string? sink;
        IReadOnlyList<string> monitorSelection;
        lock (_gate)
        {
            sink = _auxTargetSink;
            if (sink is null || !_built) return;
            monitorSelection = [.. _monitorOutputs];
            foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
            _monitorRoutes.Clear();
            if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        }
        _pw.BounceSink(sink);
        lock (_gate)
        {
            SetMonitorOutputsLocked(monitorSelection);
            WireAuxRouteLocked();
        }
    }

    /// <summary>Whether the Aux mix feeds the USB Aux port.</summary>
    public bool AuxPortEnabled { get { lock (_gate) return _auxPortEnabled; } }

    public void SetAuxPortEnabled(bool on)
    {
        lock (_gate)
        {
            _auxPortEnabled = on;
            if (_built) WireAuxRouteLocked();
        }
    }

    /// <summary>Re-wire the aux route after a hotplug; true when established.</summary>
    public bool EnsureAuxRoute()
    {
        lock (_gate)
        {
            if (!_built || !_auxPortEnabled || _auxRoute is not null) return false;
            WireAuxRouteLocked();
            return _auxRoute is not null;
        }
    }

    private string? _inputHint;
    private int _lowCutHz;                 // 0 = off; software low cut on the first XLR channel
    private FilterHandle? _lowCut;
    private PortLink? _lowCutOut;          // filter source half into the channel sink

    /// <summary>Software low cut frequency (0, 80, or 120 Hz; 0 = off).</summary>
    public int LowCutHz { get { lock (_gate) return _lowCutHz; } }

    private bool _lowCutApplicable = true;

    /// <summary>
    /// Whether the soft low cut may engage: false while the active device has
    /// a hardware low cut (stacking both would double-filter). The stored
    /// frequency survives, so switching back re-engages it.
    /// </summary>
    public void SetLowCutApplicable(bool applicable)
    {
        lock (_gate)
        {
            if (_lowCutApplicable == applicable) return;
            _lowCutApplicable = applicable;
            if (_built && _lowCutHz > 0) WireInputFeedsLocked();
        }
    }

    // Software ClipGuard: a hard limiter at -3 dB in the mic filter chain,
    // for devices whose ClipGuard runs host-side in the vendor app.
    private bool _softClipGuard;
    private bool _clipGuardApplicable = true;

    /// <summary>Whether the software ClipGuard is enabled.</summary>
    public bool SoftClipGuard { get { lock (_gate) return _softClipGuard; } }

    public void SetSoftClipGuard(bool on)
    {
        lock (_gate)
        {
            if (_softClipGuard == on) return;
            _softClipGuard = on;
            if (_built) WireInputFeedsLocked();
        }
    }

    /// <summary>False while the active device has the hardware ClipGuard.</summary>
    public void SetClipGuardApplicable(bool applicable)
    {
        lock (_gate)
        {
            if (_clipGuardApplicable == applicable) return;
            _clipGuardApplicable = applicable;
            if (_built && _softClipGuard) WireInputFeedsLocked();
        }
    }

    /// <summary>
    /// Set the software low cut for the first XLR channel: a host-side
    /// high-pass for devices whose DSP lives in the vendor app (the XLR
    /// Dock), matching Wave Link's 80/120 Hz choices. Devices with a real
    /// hardware low cut never see this path; the UI gates it by capability.
    /// </summary>
    public void SetLowCutHz(int hz)
    {
        if (hz is not (0 or 80 or 120)) return;
        lock (_gate)
        {
            if (_lowCutHz == hz) return;
            _lowCutHz = hz;
            if (_built) WireInputFeedsLocked();
        }
    }

    /// <summary>
    /// Name fragment of the interface whose capture should feed the input
    /// channels (the daemon's active device). A change re-wires the feeds.
    /// </summary>
    public void SetInputDeviceHint(string? hint)
    {
        lock (_gate)
        {
            if (_inputHint == hint) return;
            _inputHint = hint;
            if (_built) WireInputFeedsLocked();
        }
    }

    /// <summary>
    /// Re-wire the hardware input feeds if none are connected (the interface
    /// was absent at build time or was replugged). Returns true when feeds
    /// were (re)established.
    /// </summary>
    public bool EnsureInputFeeds()
    {
        lock (_gate)
        {
            if (!_built || _inputFeeds.Count > 0) return false;
            WireInputFeedsLocked();
            return _inputFeeds.Count > 0;
        }
    }

    /// <summary>Every sink and source the user can pick, real or virtual.</summary>
    public IReadOnlyList<AudioNode> ListDevices() => _pw.ListDevices();

    /// <summary>Close and reopen an output device's stream (see adapter).</summary>
    public void BounceOutput(string sinkName) => _pw.BounceSink(sinkName);

    /// <summary>Current user choices, for persisting.</summary>
    public MixerSettings ExportSettings()
    {
        lock (_gate)
        {
            return new MixerSettings
            {
                MixVolumes = new Dictionary<string, double>(_mixVolume),
                MixMuted = [.. _mixMuted],
                Levels = new Dictionary<string, double>(_levels),
                ChannelMuted = [.. _muted],
                MonitorOutputs = [.. _monitorOutputs],
                AppOverrides = new Dictionary<string, string>(Matcher.Overrides),
                KnownApps = [.. _apps.Values.Select(a => new SavedApp(a.Identity, a.Label, a.ChannelId))],
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
            };
        }
    }

    /// <summary>Apply saved choices onto the built graph.</summary>
    public void ApplySettings(MixerSettings s)
    {
        lock (_gate)
        {
            if (!_built) return;

            foreach ((string mixId, double vol) in s.MixVolumes)
                if (_mixVolume.ContainsKey(mixId)) _mixVolume[mixId] = Math.Clamp(vol, 0, 1);
            _mixMuted.Clear();
            foreach (string mixId in s.MixMuted) _mixMuted.Add(mixId);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            _muted.Clear();
            foreach (string cell in s.ChannelMuted)
                if (_cells.Contains(cell)) _muted.Add(cell);

            foreach ((string identity, string channelId) in s.AppOverrides)
                Matcher.SetOverride(Sanitize(identity), channelId);

            // Remembered apps come back inactive until a stream appears.
            // Identities saved before the "(deleted)" fix are migrated here so
            // an app does not appear twice after its binary was updated.
            foreach (SavedApp app in s.KnownApps)
            {
                string identity = Sanitize(app.Identity);
                if (PipeWireAdapter.IsPlumbingIdentity(identity)) continue;   // pre-filter leftovers
                if (!_apps.ContainsKey(identity))
                    _apps[identity] = new StreamAssignment(0, 0, Sanitize(app.Label), identity, app.ChannelId) { Active = false, Running = false };
            }

            static string Sanitize(string v) => v.EndsWith(" (deleted)", StringComparison.Ordinal) ? v[..^10] : v;

            foreach (MixDefinition mix in _config.Mixes) ReapplyMixLocked(mix.Id);

            IReadOnlyList<string> savedOutputs = s.MonitorOutputs is { Count: > 0 }
                ? s.MonitorOutputs
                : s.MonitorOutput is not null ? [s.MonitorOutput] : [];
            if (savedOutputs.Count > 0) SetMonitorOutputsLocked(savedOutputs);
            _enforcedSink = s.EnforcedDefaultSink;
            _enforcedSource = s.EnforcedDefaultSource;

            // Migration: before the Aux mix existed, "USB Aux Out" was a
            // monitor destination; carry that intent over once.
            _auxPortEnabled = s.AuxPortEnabled
                ?? (s.MonitorOutputs.Any(o => o.EndsWith("#usbaux", StringComparison.Ordinal)) ||
                    (s.MonitorOutput?.EndsWith("#usbaux", StringComparison.Ordinal) ?? false));
            WireAuxRouteLocked();

            bool rewire = false;
            if (s.LowCutHz is 80 or 120 && _lowCutHz != s.LowCutHz)
            {
                _lowCutHz = s.LowCutHz;
                rewire = true;
            }
            if (s.SoftClipGuard && !_softClipGuard)
            {
                _softClipGuard = true;
                rewire = true;
            }
            if (rewire) WireInputFeedsLocked();
        }
    }

    /// <summary>The current mixer scene, for saving into a profile.</summary>
    public MixerScene ExportScene()
    {
        lock (_gate)
        {
            return new MixerScene
            {
                MixVolumes = new Dictionary<string, double>(_mixVolume),
                MixMuted = [.. _mixMuted],
                Levels = new Dictionary<string, double>(_levels),
                ChannelMuted = [.. _muted],
                MonitorOutputs = [.. _monitorOutputs],
                AuxPortEnabled = _auxPortEnabled,
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
            };
        }
    }

    /// <summary>
    /// Recall a profile's mixer scene. Unlike <see cref="ApplySettings"/> this
    /// touches only scene state: app routing, the registry, and the enforced
    /// system defaults stay exactly as they are.
    /// </summary>
    public void ApplyScene(MixerScene s)
    {
        lock (_gate)
        {
            if (!_built) return;

            foreach ((string mixId, double vol) in s.MixVolumes)
                if (_mixVolume.ContainsKey(mixId)) _mixVolume[mixId] = Math.Clamp(vol, 0, 1);
            _mixMuted.Clear();
            foreach (string mixId in s.MixMuted) _mixMuted.Add(mixId);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            _muted.Clear();
            foreach (string cell in s.ChannelMuted)
                if (_cells.Contains(cell)) _muted.Add(cell);

            foreach (MixDefinition mix in _config.Mixes) ReapplyMixLocked(mix.Id);

            if (s.MonitorOutputs.Count > 0) SetMonitorOutputsLocked(s.MonitorOutputs);
            _auxPortEnabled = s.AuxPortEnabled;
            WireAuxRouteLocked();

            bool rewire = false;
            if (s.LowCutHz is int hz && hz is 0 or 80 or 120 && _lowCutHz != hz)
            {
                _lowCutHz = hz;
                rewire = true;
            }
            if (s.SoftClipGuard is bool scg && _softClipGuard != scg)
            {
                _softClipGuard = scg;
                rewire = true;
            }
            if (rewire) WireInputFeedsLocked();
        }
        if (s.OutputVolume is double v) SetOutputVolume(v);
    }

    /// <summary>Volume of the selected output devices (0..1), applied to each.</summary>
    public void SetOutputVolume(double volume)
    {
        lock (_gate)
        {
            if (_monitorOutputs.Count == 0) return;
            foreach (string sink in _monitorOutputs.Select(StripMarker).Distinct())
            {
                try { _pw.SetSinkVolume(sink, volume); }
                catch (InvalidOperationException) { /* device gone */ }
            }
            _outputVolume = volume;
        }
    }

    private static string StripMarker(string name)
    {
        int marker = name.IndexOf('#');
        return marker >= 0 ? name[..marker] : name;
    }

    /// <summary>Enforced system default devices (sink, source); null = off.</summary>
    public (string? Sink, string? Source) EnforcedDefaults
    {
        get { lock (_gate) return (_enforcedSink, _enforcedSource); }
    }

    /// <summary>
    /// Choose the devices to hold as system defaults. Applied immediately and
    /// then re-asserted on every sweep.
    /// </summary>
    public void SetEnforcedDefaults(string? sink, string? source)
    {
        lock (_gate)
        {
            _enforcedSink = string.IsNullOrEmpty(sink) ? null : sink;
            _enforcedSource = string.IsNullOrEmpty(source) ? null : source;
        }
        EnforceDefaults();
    }

    /// <summary>
    /// Re-assert the enforced defaults. WirePlumber auto-switches defaults to
    /// new devices and replays remembered preferences, so a one-time set is not
    /// enough: this runs every sweep, exactly like Wave Link holds its devices.
    /// </summary>
    public bool EnforceDefaults()
    {
        string? sink, source;
        lock (_gate) { sink = _enforcedSink; source = _enforcedSource; }
        bool corrected = false;
        try
        {
            if (sink is not null && _pw.GetDefaultSink() != sink)
            {
                _pw.SetDefaultSink(sink);
                corrected = true;
            }
            if (source is not null && _pw.GetDefaultSource() != source)
            {
                _pw.SetDefaultSource(source);
                corrected = true;
            }
        }
        catch (InvalidOperationException) { /* device currently absent */ }
        return corrected;
    }

    /// <summary>
    /// Refresh the cached device volumes; true when either moved (externally or
    /// through us), so the daemon knows to push fresh state.
    /// </summary>
    public bool SyncDeviceVolumes()
    {
        lock (_gate)
        {
            string? first = _monitorOutputs.FirstOrDefault();
            double? outV = first is null ? null : _pw.GetSinkVolume(first);
            bool changed = Differs(outV, _outputVolume);
            _outputVolume = outV;
            return changed;
        }

        static bool Differs(double? a, double? b)
            => a.HasValue != b.HasValue || (a.HasValue && Math.Abs(a.Value - b!.Value) > 0.005);
    }

    /// <summary>First selected monitor output, or null (legacy single view).</summary>
    public string? MonitorOutput { get { lock (_gate) return _monitorOutputs.FirstOrDefault(); } }

    /// <summary>All selected monitor outputs, in selection order.</summary>
    public IReadOnlyList<string> MonitorOutputs { get { lock (_gate) return [.. _monitorOutputs]; } }

    /// <summary>
    /// Send the monitor mix to one output device (or none). Kept for clients
    /// that think in a single monitor destination.
    /// </summary>
    public void SetMonitorOutput(string? sinkName)
        => SetMonitorOutputs(sinkName is null ? [] : [sinkName]);

    /// <summary>
    /// Send the monitor mix to any set of output devices at once. Any sink
    /// works, virtual ones included; an empty list disconnects the monitor.
    /// </summary>
    public void SetMonitorOutputs(IReadOnlyList<string> sinkNames)
    {
        lock (_gate) { if (_built) SetMonitorOutputsLocked(sinkNames); }
    }

    private void SetMonitorOutputsLocked(IReadOnlyList<string> sinkNames)
    {
        foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
        _monitorRoutes.Clear();
        _monitorOutputs.Clear();
        foreach (string name in sinkNames.Where(n => !string.IsNullOrEmpty(n)).Distinct())
        {
            // The aux port is owned by the Aux mix now; old saved selections
            // that still carry it as a monitor destination are dropped.
            if (name.EndsWith("#usbaux", StringComparison.Ordinal)) continue;
            _monitorOutputs.Add(name);
        }
        MixDefinition? monitor = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
        if (monitor is null) return;
        foreach ((string key, string target) in MonitorRouteTargetsLocked())
            _monitorRoutes[key] = _pw.RouteMixToOutput(monitor.SinkName, target);
    }

    /// <summary>
    /// One route per selected monitor destination, keyed so pseudo-outputs of
    /// one device share a route when they ride the same USB return pair: the
    /// analog outputs (hp1/hp2/lineout) all use the monitor-bus pair.
    /// </summary>
    private IEnumerable<(string Key, string Target)> MonitorRouteTargetsLocked()
    {
        var seen = new HashSet<string>();
        foreach (string name in _monitorOutputs)
        {
            int marker = name.IndexOf('#');
            string key = marker < 0 ? name : name[..marker] + "#bus";
            if (seen.Add(key)) yield return (key, name);
        }
    }

    /// <summary>
    /// Verify the monitor mix's device links and repair what died: a USB
    /// output that re-enumerates (suspend, reset, profile change) takes its
    /// node and every link on it along, and a device absent at wiring time
    /// got no links at all. Runs every sweep; true when something changed.
    /// </summary>
    public bool EnsureMonitorRoutes()
    {
        lock (_gate)
        {
            if (!_built || _monitorOutputs.Count == 0) return false;
            MixDefinition? monitor = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
            if (monitor is null) return false;
            bool changed = false;
            foreach ((string key, string target) in MonitorRouteTargetsLocked())
            {
                PortLink? route = _monitorRoutes.GetValueOrDefault(key);
                if (route is { Pairs.Count: > 0 })
                {
                    LinkHealth health = _pw.EnsureLinks(route);
                    if (health == LinkHealth.Healthy) continue;
                    if (health == LinkHealth.Relinked) { changed = true; continue; }
                    _pw.Unlink(route);   // Broken: the port names themselves are stale
                }
                _monitorRoutes[key] = _pw.RouteMixToOutput(monitor.SinkName, target);
                changed |= _monitorRoutes[key].Pairs.Count > 0;
            }
            return changed;
        }
    }

    /// <summary>Level of one channel in one mix (0..1).</summary>
    public void SetLevel(string channelId, string mixId, double level)
    {
        lock (_gate)
        {
            string cell = Cell(channelId, mixId);
            if (!_cells.Contains(cell)) return;
            _levels[cell] = Math.Clamp(level, 0.0, 1.0);
            ApplyCellLocked(channelId, mixId);
        }
    }

    /// <summary>Mute/unmute one channel within one mix only.</summary>
    public void SetChannelMuted(string channelId, string mixId, bool muted)
    {
        lock (_gate)
        {
            string cell = Cell(channelId, mixId);
            if (!_cells.Contains(cell)) return;
            if (muted) _muted.Add(cell); else _muted.Remove(cell);
            ApplyCellLocked(channelId, mixId);
        }
    }

    /// <summary>Master level for a mix, scaling every channel feeding it.</summary>
    public void SetMixVolume(string mixId, double volume)
    {
        lock (_gate)
        {
            _mixVolume[mixId] = Math.Clamp(volume, 0.0, 1.0);
            ReapplyMixLocked(mixId);
        }
    }

    public void SetMixMuted(string mixId, bool muted)
    {
        lock (_gate)
        {
            if (muted) _mixMuted.Add(mixId); else _mixMuted.Remove(mixId);
            ReapplyMixLocked(mixId);
        }
    }

    /// <summary>The matcher deciding which channel a new app stream joins.</summary>
    public StreamMatcher Matcher { get; } = new();

    /// <summary>Streams seen on the last sweep, with the channel each landed in.</summary>
    public IReadOnlyList<StreamAssignment> Streams
    {
        get { lock (_gate) return [.. _streams.Values]; }
    }

    // Known applications keyed by identity: active ones have a live stream,
    // inactive ones are remembered so their routing stays editable and the
    // list is stable. _streams tracks the live stream ids already placed.
    private readonly Dictionary<string, StreamAssignment> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, StreamAssignment> _streams = [];

    /// <summary>
    /// Look for application streams and route new ones to their channel. Called
    /// on a timer by the daemon. Returns true when anything changed. Streams the
    /// mixer already placed are left alone so manual overrides survive.
    /// </summary>
    public bool SyncStreams()
    {
        if (!_built) return false;
        IReadOnlyList<AudioStream> live = _pw.ListStreams();
        bool changed = false;

        lock (_gate)
        {
            var seen = new HashSet<int>();
            var liveIdentities = new HashSet<string>();
            foreach (AudioStream s in live)
            {
                seen.Add(s.Id);
                liveIdentities.Add(s.Identity);
                if (_streams.ContainsKey(s.Id)) continue;

                string channelId = Matcher.Match(s);
                ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId)
                                        ?? _config.Channels.FirstOrDefault();
                if (ch is null) continue;

                try { _pw.MoveStreamToSink(s.Serial, ch.SinkName); }
                catch (InvalidOperationException) { continue; }

                var placed = new StreamAssignment(s.Id, s.Serial, s.Label, s.Identity, ch.Id);
                _streams[s.Id] = placed;
                // Transient plumbing (Wine's probe streams, bare runtime
                // binaries) is routed but never remembered as an app.
                if (!PipeWireAdapter.IsPlumbingIdentity(s.Identity)) _apps[s.Identity] = placed;
                changed = true;
            }

            foreach (int gone in _streams.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _streams.Remove(gone);
                changed = true;
            }

            // Every running audio-capable app is listed even before it plays:
            // PipeWire clients cover "running", streams cover "playing".
            var runningIdentities = new HashSet<string>();
            foreach (AudioStream client in _pw.ListClients())
            {
                string identity = client.Identity;
                if (PipeWireAdapter.IsPlumbingIdentity(identity)) continue;
                runningIdentities.Add(identity);
                if (!_apps.ContainsKey(identity))
                {
                    _apps[identity] = new StreamAssignment(0, 0, client.Label, identity,
                        Matcher.Match(client)) { Active = false, Running = true };
                    changed = true;
                }
                else if (!_apps[identity].Active && _apps[identity].Label != client.Label &&
                         (client.Label.Length < _apps[identity].Label.Length ||
                          string.Equals(_apps[identity].Label, identity, StringComparison.OrdinalIgnoreCase)))
                {
                    // Heal stale or placeholder labels from a live client, but
                    // prefer the shortest name: apps register helper clients
                    // like "Google Chrome input" alongside the real one.
                    _apps[identity] = _apps[identity] with { Label = client.Label };
                    changed = true;
                }
            }

            // Reconcile flags; remembered apps stay listed when gone entirely.
            foreach ((string identity, StreamAssignment app) in _apps.ToList())
            {
                bool activeNow = liveIdentities.Contains(identity);
                bool runningNow = activeNow || runningIdentities.Contains(identity);
                if (app.Active != activeNow || app.Running != runningNow)
                {
                    _apps[identity] = app with
                    {
                        Active = activeNow, Running = runningNow,
                        Id = activeNow ? app.Id : 0, Serial = activeNow ? app.Serial : 0,
                    };
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Drop an application from the registry and forget its channel override.
    /// A still-running app simply re-registers on the next sweep.
    /// </summary>
    public void ForgetApp(string identity)
    {
        lock (_gate)
        {
            _apps.Remove(identity);
            Matcher.RemoveOverride(identity);
        }
    }

    /// <summary>
    /// Route an application (by identity) to a channel: remembered for every
    /// future stream, and applied to its live streams right away if any.
    /// </summary>
    public void AssignApp(string identity, string channelId, string? label = null)
    {
        lock (_gate)
        {
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId);
            if (ch is null || string.IsNullOrWhiteSpace(identity)) return;
            Matcher.SetOverride(identity, channelId);

            foreach ((int id, StreamAssignment placed) in _streams.ToList())
                if (placed.Identity == identity)
                {
                    try { _pw.MoveStreamToSink(placed.Serial, ch.SinkName); }
                    catch (InvalidOperationException) { continue; }
                    _streams[id] = placed with { ChannelId = channelId };
                }

            if (_apps.TryGetValue(identity, out StreamAssignment? app))
                _apps[identity] = app with { ChannelId = channelId };
            else
                // Pre-registered by hand (e.g. from the installed-apps picker):
                // listed silent until its first stream shows up and confirms.
                _apps[identity] = new StreamAssignment(0, 0, label ?? identity, identity, channelId) { Active = false, Running = false };
        }
    }

    /// <summary>
    /// Move one stream to a channel by hand and remember the choice for the next
    /// time the same application starts.
    /// </summary>
    public void AssignStream(int streamId, string channelId)
    {
        lock (_gate)
        {
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId);
            if (ch is null) return;

            if (_streams.TryGetValue(streamId, out StreamAssignment? existing))
            {
                _pw.MoveStreamToSink(existing.Serial, ch.SinkName);
                Matcher.SetOverride(existing.Identity, channelId);
                _streams[streamId] = existing with { ChannelId = channelId };
                return;
            }
            _pw.MoveStreamToSink(streamId, ch.SinkName);
        }
    }

    public MixerState Snapshot()
    {
        lock (_gate)
        {
            return new MixerState
            {
                Mixes = [.. _config.Mixes.Select(m => new MixStatus(
                    m.Id, m.Name,
                    _mixVolume.GetValueOrDefault(m.Id, 1.0),
                    _mixMuted.Contains(m.Id)))],
                Channels = [.. _config.Channels.Select(c => new ChannelStatus(
                    c.Id, c.Name,
                    _config.Mixes.ToDictionary(m => m.Id, m => _levels.GetValueOrDefault(Cell(c.Id, m.Id), 0.0)),
                    [.. _config.Mixes.Where(m => _muted.Contains(Cell(c.Id, m.Id))).Select(m => m.Id)]))],
                MonitorOutput = _monitorOutputs.FirstOrDefault(),
                MonitorOutputs = [.. _monitorOutputs],
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                Streams = [.. _apps.Values
                    .OrderByDescending(a => a.Active).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)],
            };
        }
    }

    /// <summary>Cell level x mix master, applied to the combine leg's stream.</summary>
    private void ApplyCellLocked(string channelId, string mixId)
    {
        string cell = Cell(channelId, mixId);
        double level = _levels.GetValueOrDefault(cell, 0.0) * _mixVolume.GetValueOrDefault(mixId, 1.0);
        bool muted = _muted.Contains(cell) || _mixMuted.Contains(mixId);

        if (!_legIndex.TryGetValue(cell, out int idx)) { DiscoverLegsLocked(); if (!_legIndex.TryGetValue(cell, out idx)) return; }
        try
        {
            _pw.SetSinkInputVolume(idx, level);
            _pw.SetSinkInputMuted(idx, muted);
        }
        catch (InvalidOperationException)
        {
            // The leg's index changed (combine reconnected); rediscover once.
            DiscoverLegsLocked();
            if (_legIndex.TryGetValue(cell, out idx))
            {
                try { _pw.SetSinkInputVolume(idx, level); _pw.SetSinkInputMuted(idx, muted); }
                catch (InvalidOperationException) { /* give up until next change */ }
            }
        }
    }

    private void ReapplyMixLocked(string mixId)
    {
        foreach (ChannelDefinition ch in _config.Channels)
            ApplyCellLocked(ch.Id, mixId);
    }

    private void TearDownLocked()
    {
        _meters.Dispose();
        _meters = new MeterReader();   // Dispose is terminal; a rebuild needs a fresh reader
        foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
        _monitorRoutes.Clear();
        _monitorOutputs.Clear();
        if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        foreach (PortLink feed in _inputFeeds) _pw.Unlink(feed);
        _inputFeeds.Clear();
        RemoveLowCutLocked();
        _inputDevice = null;
        _pw.TearDown();     // unloads modules in reverse order: combines, then mixes
        _combineModules.Clear();
        _legIndex.Clear();
        _streams.Clear();
        _cells.Clear();
        _levels.Clear();
        _muted.Clear();
        _mixVolume.Clear();
        _mixMuted.Clear();
        _built = false;
    }

    public void TearDown()
    {
        lock (_gate) TearDownLocked();
    }

    public void Dispose()
    {
        TearDown();
        _meters.Dispose();
    }
}
