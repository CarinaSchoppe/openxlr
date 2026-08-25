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

    // The monitor mix's route to the output device (direct port links) and the
    // hardware microphone's feed into the mic channel.
    private PortLink? _monitorRoute;
    private string? _monitorOutput;
    private PortLink? _micFeed;

    // "Input" in the UI: the capture device feeding the mic channel. The system
    // default source is a separate concept, owned solely by the enforced
    // defaults below (two controls must not share one property).
    private string? _micDevice;

    // Cached hardware volumes of the selected output and input devices, so
    // external changes (KDE applet, hardware knobs) can be detected and pushed.
    private double? _outputVolume;
    private double? _inputVolume;

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

            if (monitorOutputSink is not null) SetMonitorOutputLocked(monitorOutputSink);
            _ = previousDefaultSource;   // defaults are governed by enforcement only
            SetMicInputLocked(defaultSource);
        }
    }

    /// <summary>
    /// Wire a capture device into the mic channel. Passing null auto-detects the
    /// connected Wave XLR by name; a device the mixer itself created is refused
    /// (feeding a mix back into a channel is a feedback loop). Skipped when
    /// nothing suitable exists: the mic channel is then silent, nothing else is
    /// affected.
    /// </summary>
    private void SetMicInputLocked(string? sourceName)
    {
        ChannelDefinition? mic = _config.Channels.FirstOrDefault(c => c.Id == "mic");
        if (mic is null) return;

        if (sourceName is not null &&
            (sourceName.StartsWith("OpenXLR", StringComparison.Ordinal) ||
             _pw.ListDevices().FirstOrDefault(d => d.Name == sourceName) is null or { IsOwn: true }))
            sourceName = null;   // invalid or own node: fall back to auto-detect

        sourceName ??= _pw.ListDevices()
            .FirstOrDefault(d => d.Kind == AudioNodeKind.Source && !d.IsOwn &&
                                 d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))?.Name;

        if (_micFeed is not null) { _pw.Unlink(_micFeed); _micFeed = null; }
        _micDevice = sourceName;
        if (sourceName is null) return;
        _micFeed = _pw.RouteInputToChannel(sourceName, mic.SinkName);
    }

    /// <summary>Every sink and source the user can pick, real or virtual.</summary>
    public IReadOnlyList<AudioNode> ListDevices() => _pw.ListDevices();

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
                MonitorOutput = _monitorOutput,
                MicInput = _micDevice,
                AppOverrides = new Dictionary<string, string>(Matcher.Overrides),
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
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
                Matcher.SetOverride(identity, channelId);

            foreach (MixDefinition mix in _config.Mixes) ReapplyMixLocked(mix.Id);

            if (s.MonitorOutput is not null) SetMonitorOutputLocked(s.MonitorOutput);
            SetMicInputLocked(s.MicInput);
            _enforcedSink = s.EnforcedDefaultSink;
            _enforcedSource = s.EnforcedDefaultSource;
        }
    }

    /// <summary>Volume of the selected output device (0..1), or null.</summary>
    public void SetOutputVolume(double volume)
    {
        lock (_gate)
        {
            if (_monitorOutput is null) return;
            try { _pw.SetSinkVolume(_monitorOutput, volume); _outputVolume = volume; }
            catch (InvalidOperationException) { /* device gone */ }
        }
    }

    /// <summary>Volume of the mic-feed device (0..1).</summary>
    public void SetInputVolume(double volume)
    {
        lock (_gate)
        {
            if (_micDevice is null) return;
            try { _pw.SetSourceVolume(_micDevice, volume); _inputVolume = volume; }
            catch (InvalidOperationException) { /* device gone */ }
        }
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
            double? outV = _monitorOutput is null ? null : _pw.GetSinkVolume(_monitorOutput);
            double? inV = _micDevice is null ? null : _pw.GetSourceVolume(_micDevice);
            bool changed = Differs(outV, _outputVolume) || Differs(inV, _inputVolume);
            _outputVolume = outV;
            _inputVolume = inV;
            return changed;
        }

        static bool Differs(double? a, double? b)
            => a.HasValue != b.HasValue || (a.HasValue && Math.Abs(a.Value - b!.Value) > 0.005);
    }

    /// <summary>Currently selected output for the monitor mix, or null.</summary>
    public string? MonitorOutput { get { lock (_gate) return _monitorOutput; } }

    /// <summary>The capture device feeding the mic channel, or null.</summary>
    public string? MicInput { get { lock (_gate) return _micDevice; } }

    /// <summary>
    /// Send the monitor mix to a different output device. Any sink works,
    /// including virtual ones. Passing null disconnects the monitor mix.
    /// </summary>
    public void SetMonitorOutput(string? sinkName)
    {
        lock (_gate) { if (_built) SetMonitorOutputLocked(sinkName); }
    }

    /// <summary>Feed the mic channel from a different capture device.</summary>
    public void SetMicInput(string? sourceName)
    {
        lock (_gate) { if (_built) SetMicInputLocked(sourceName); }
    }

    private void SetMonitorOutputLocked(string? sinkName)
    {
        if (_monitorRoute is not null) { _pw.Unlink(_monitorRoute); _monitorRoute = null; }
        _monitorOutput = sinkName;
        if (sinkName is null) return;
        MixDefinition? monitor = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
        if (monitor is null) return;
        _monitorRoute = _pw.RouteMixToOutput(monitor.SinkName, sinkName);
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
            foreach (AudioStream s in live)
            {
                seen.Add(s.Id);
                if (_streams.ContainsKey(s.Id)) continue;

                string channelId = Matcher.Match(s);
                ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId)
                                        ?? _config.Channels.FirstOrDefault();
                if (ch is null) continue;

                try { _pw.MoveStreamToSink(s.Serial, ch.SinkName); }
                catch (InvalidOperationException) { continue; }

                _streams[s.Id] = new StreamAssignment(s.Id, s.Serial, s.Label, s.Identity, ch.Id);
                changed = true;
            }

            foreach (int gone in _streams.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _streams.Remove(gone);
                changed = true;
            }
        }
        return changed;
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
                MonitorOutput = _monitorOutput,
                MicInput = _micDevice,
                OutputVolume = _outputVolume,
                InputVolume = _inputVolume,
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                Streams = [.. _streams.Values],
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
        if (_monitorRoute is not null) { _pw.Unlink(_monitorRoute); _monitorRoute = null; }
        if (_micFeed is not null) { _pw.Unlink(_micFeed); _micFeed = null; }
        _pw.TearDown();     // unloads modules in reverse order: combines, then mixes
        _combineModules.Clear();
        _legIndex.Clear();
        _monitorOutput = null;
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
