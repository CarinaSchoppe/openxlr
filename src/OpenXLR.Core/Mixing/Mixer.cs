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
/// whole matrix needs one sink per channel plus one per mix. Everything is
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

    // Desired many-to-many output routes are separate from their live links:
    // an unplugged destination retains intent by stable ID without leaving a
    // ghost PipeWire node. Links are keyed by mix and destination.
    private readonly List<OutputRouteDefinition> _outputRoutes = [];
    private readonly Dictionary<string, PortLink> _outputRouteLinks = [];
    private readonly Dictionary<string, FilterHandle> _outputRouteDelays = [];
    private readonly Dictionary<string, PortLink> _outputRouteDelayInputs = [];
    private readonly Dictionary<string, int> _routeCompensationSamples = [];
    private int _graphQuantumSamples = 1024;
    // Compatibility projection for older clients that select one listened mix
    // and a list of runtime node names.
    private readonly List<string> _monitorOutputs = [];
    private string _monitoredMixId = "monitor";
    private readonly Dictionary<string, PortLink> _inputFeeds = [];
    private string? _inputDevice;   // the capture device the feeds come from
    private long _inputChainGeneration;

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

    /// <summary>
    /// Change only the presentation order of application channels. Stable ids,
    /// PipeWire nodes, routes, and audio keep running unchanged.
    /// </summary>
    public void ReorderUserChannels(IReadOnlyList<string> orderedIds)
    {
        lock (_gate)
        {
            var current = _config.Channels.Where(c => c.InputPair is null).ToList();
            ValidateOrder("reorderChannels", orderedIds, current.Select(c => c.Id));
            var byId = current.ToDictionary(c => c.Id, StringComparer.Ordinal);
            _config = _config with
            {
                Channels = [.. _config.Channels.Where(c => c.InputPair is not null),
                    .. orderedIds.Select(id => byId[id])],
            };
        }
    }

    /// <summary>
    /// Change only the presentation order of user-created output mixes. The
    /// structural Monitor and Aux buses keep their fixed edge positions.
    /// </summary>
    public void ReorderUserMixes(IReadOnlyList<string> orderedIds)
    {
        lock (_gate)
        {
            var current = _config.Mixes.Where(m => m.Kind == MixKind.VirtualMic).ToList();
            ValidateOrder("reorderMixes", orderedIds, current.Select(m => m.Id));
            var byId = current.ToDictionary(m => m.Id, StringComparer.Ordinal);
            _config = _config with
            {
                Mixes = [.. _config.Mixes.Where(m => m.Kind == MixKind.Monitor),
                    .. orderedIds.Select(id => byId[id]),
                    .. _config.Mixes.Where(m => m.Kind == MixKind.AuxPort)],
            };
        }
    }

    private static void ValidateOrder(string command, IReadOnlyList<string> requested,
        IEnumerable<string> existing)
    {
        var expected = existing.ToHashSet(StringComparer.Ordinal);
        bool valid = requested.Count == expected.Count &&
            requested.Distinct(StringComparer.Ordinal).Count() == requested.Count &&
            requested.All(expected.Contains);
        if (!valid)
            throw new InvalidOperationException($"{command}: order must contain every editable item exactly once");
    }

    private static string Cell(string channel, string mix) => $"{channel}|{mix}";
    private static string OutputRouteKey(string mix, string destination) => $"{mix}|{destination}";

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
    /// the initially listened mix feeds. Default-device enforcement is owned by the daemon,
    /// not graph construction, so rebuilding does not change the user's policy.
    /// </summary>
    public void Build(MixerConfig config, string? monitorOutputSink = null)
    {
        lock (_gate)
        {
            if (_built) TearDownLocked();
            _config = config;
            _monitoredMixId = ResolveMonitoredMixId(_monitoredMixId);
            _graphQuantumSamples = _pw.GetGraphQuantumSamples();

            // A crashed or killed daemon never runs its teardown, and loading
            // over its leftover nodes fails the whole build with a name
            // collision. Clear any stray OpenXLR modules first.
            _pw.UnloadStaleModules("OpenXLR_");

            // Mixes first: the cells attach to them as masters.
            foreach (MixDefinition mix in config.Mixes)
            {
                _pw.CreateNullSink(mix.SinkName, $"OpenXLR {mix.Name} (internal mix bus)", isInternal: true);
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
                if (ch.InputPair is null) _pw.CreateNullSink(ch.SinkName, $"OpenXLR {ch.Name}");
                _combineModules[ch.Id] = _pw.CreateCombineSink(ch.FanOutSinkName,
                    config.Mixes.Select(m => m.SinkName),
                    $"OpenXLR {ch.Name} (internal distribution)", needsMonitor: ch.InputPair is not null);
            }
            DiscoverLegsLocked();

            // Push initial fader values.
            foreach (MixDefinition mix in config.Mixes) ReapplyMixLocked(mix.Id);

            // Publish non-monitor mixes as selectable capture devices. Each
            // reads a post sink fed from the mix (directly, or through the
            // mix's insert chain), so adding inserts later never recreates
            // the capture device an app is recording from.
            foreach (MixDefinition mix in config.Mixes.Where(m => m.Kind == MixKind.VirtualMic))
            {
                _pw.CreateNullSink(mix.PostSinkName, $"OpenXLR {mix.Name} (internal capture tap)", isInternal: true);
                _pw.CreateVirtualMic(mix.VirtualMicName, $"{mix.PostSinkName}.monitor", $"OpenXLR {mix.Name}");
            }

            // Meter every channel and mix so the UI can show what is flowing.
            foreach (ChannelDefinition ch in config.Channels) _meters.Add($"ch:{ch.Id}", ch.SinkName);
            foreach (MixDefinition mix in config.Mixes) _meters.Add($"mix:{mix.Id}", mix.SinkName);

            _built = true;

            if (monitorOutputSink is not null) SetMonitorOutputsLocked([monitorOutputSink]);
            WireInputFeedsLocked();
            WireAuxRouteLocked();
            foreach (MixDefinition mix in config.Mixes) WireMixChainLocked(mix);
            foreach (ChannelDefinition channel in config.Channels.Where(c => c.InputPair is null)) WireAppChainLocked(channel);
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
        // UCM split sources (".HiFi__Mic2__source" and friends) rank last: the
        // channels are wired by pair offset on the raw multichannel node, and
        // a split's first match could be the wrong input entirely.
        var sources = _pw.ListDevices()
            .Where(d => d.Kind == AudioNodeKind.Source && !d.IsOwn)
            .OrderBy(d => d.Name.Contains(".HiFi__", StringComparison.Ordinal) ? 1 : 0)
            .ToList();
        // Prefer the interface the daemon actively drives (the hint), so a
        // device switch moves the channel feeds with it; fall back to any
        // Wave XLR so the mixer still works when no device is connected.
        string? previousInput = _inputDevice;
        string? nextInput = (_inputHint is null ? null : sources.FirstOrDefault(
                d => d.Name.Contains(_inputHint, StringComparison.OrdinalIgnoreCase))?.Name)
            ?? sources.FirstOrDefault(
                d => d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))?.Name;
        if (nextInput is null)
        {
            foreach (PortLink feed in _inputFeeds.Values) _pw.Unlink(feed);
            _inputFeeds.Clear();
            RemoveInputChainsLocked();
            _inputDevice = null;
            return;
        }

        // Console rule: a newly patched input comes up muted. Switching the
        // feed device once put a hot mic straight into the monitor outputs
        // (a feedback howl through the speakers), so the hardware channels'
        // monitor sends start muted after a device change and the user
        // unmutes deliberately.
        if (previousInput is not null && previousInput != nextInput)
        {
            MixDefinition? mon = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
            if (mon is not null)
                foreach (ChannelDefinition hw in _config.Channels.Where(c => c.InputPair is not null))
                {
                    _muted.Add(Cell(hw.Id, mon.Id));
                    ApplyCellLocked(hw.Id, mon.Id);
                }
        }
        // Build the replacement graph under unique node names while the old
        // one is still carrying audio. Only after every required port and link
        // exists do we remove the previous routes. A missing LADSPA/LV2 plugin
        // can therefore report an error without muting the microphone.
        var nextFeeds = new Dictionary<string, PortLink>();
        var nextChains = new Dictionary<string, FilterHandle>();
        var nextChainOuts = new Dictionary<string, PortLink>();
        var nextSidechains = new Dictionary<string, PortLink>();
        long generation = ++_inputChainGeneration;
        try
        {
            foreach (ChannelDefinition ch in _config.Channels.Where(c => c.InputPair is not null))
            {
                // The soft low cut and ClipGuard belong to the first XLR
                // channel only; inserts can sit on either mono XLR channel.
                bool lc = ch.InputPair == 0 && _lowCutHz > 0 && _lowCutApplicable;
                bool cg = ch.InputPair == 0 && _softClipGuard && _clipGuardApplicable;
                List<InsertDefinition> inserts = IsInsertChannel(ch.Id) ? RunnableInsertsFor(ch.Id) : [];
                bool anyInsert = inserts.Any(i => !i.Bypass && PluginRegistry.Find(i) is not null);
                if (lc || cg || anyInsert)
                {
                    _insertErrors.Remove(ch.Id);
                    FilterHandle chain;
                    string chainId = $"{ch.Id}_{generation}";
                    try
                    {
                        chain = ch.Id == "aux" ? _pw.CreateMixChain(chainId, "OpenXLR Aux In Inserts", inserts)
                            : _pw.CreateMicFilter(chainId, lc ? _lowCutHz : 0, cg, inserts);
                        foreach ((string key, PortLink link) in CreateSidechainLinksLocked(ch.Id, chain))
                            nextSidechains[key] = link;
                    }
                    catch (Exception ex) when (anyInsert)
                    {
                        RecordChainFailureLocked(ch.Id, inserts, ex);
                        // Insert failures fall back to the built-in DSP, or to
                        // a plain feed when this chain contained inserts only.
                        _insertErrors[ch.Id] = ex.Message;
                        if (!lc && !cg)
                        {
                            if (previousInput == nextInput && !_chains.ContainsKey(ch.Id)
                                && _inputFeeds.TryGetValue(ch.Id, out PortLink? existingFeed)
                                && _pw.EnsureLinks(existingFeed) != LinkHealth.Broken)
                                nextFeeds[ch.Id] = existingFeed;
                            else
                            {
                                PortLink plain = _pw.RouteInputToChannel(nextInput, ch.SinkName, ch.InputPair!.Value);
                                // No such capture pair on this device (see below): silent channel.
                                if (plain.Pairs.Count == 0) continue;
                                nextFeeds[ch.Id] = plain;
                            }
                            continue;
                        }
                        chain = _pw.CreateMicFilter(chainId + "_builtin", lc ? _lowCutHz : 0, cg);
                    }

                    PortLink into = _pw.RouteInputToChannel(nextInput, chain.SinkName, ch.InputPair!.Value);
                    if (into.Pairs.Count == 0)
                    {
                        // The device has no capture pair at this offset (a
                        // stereo interface has no XLR 2 or Aux In pair): the
                        // channel stays silent, and the chain built for it is
                        // not needed.
                        string sidechainPrefix = ch.Id + '\0';
                        foreach (string key in nextSidechains.Keys.Where(key =>
                                     key.StartsWith(sidechainPrefix, StringComparison.Ordinal)).ToList())
                        {
                            _pw.Unlink(nextSidechains[key]);
                            nextSidechains.Remove(key);
                        }
                        _pw.StopFilter(chain);
                        continue;
                    }
                    nextChains[ch.Id] = chain;
                    MarkRunningPluginsHealthyLocked(ch.Id, chain);
                    PortLink onward = _pw.LinkNodes(chain.SourceName, "capture", ch.SinkName, "playback");
                    if (onward.Pairs.Count == 0)
                    {
                        nextFeeds[ch.Id] = into;   // rolled back with the rest
                        throw new InvalidOperationException($"could not connect the filter chain for {ch.Id}");
                    }
                    nextFeeds[ch.Id] = into;
                    nextChainOuts[ch.Id] = onward;
                    continue;
                }

                // Reuse a healthy direct feed when neither the source nor the
                // DSP changed. Trying to create the same pw-link again returns
                // EEXIST and would look like a failed route.
                if (previousInput == nextInput && !_chains.ContainsKey(ch.Id)
                    && _inputFeeds.TryGetValue(ch.Id, out PortLink? directFeed)
                    && _pw.EnsureLinks(directFeed) != LinkHealth.Broken)
                {
                    nextFeeds[ch.Id] = directFeed;
                    continue;
                }
                PortLink feed = _pw.RouteInputToChannel(nextInput, ch.SinkName, ch.InputPair!.Value);
                // The default config always defines XLR 1, XLR 2 and Aux In
                // (pairs 0, 1, 2); a device with fewer capture pairs has no
                // ports at the higher offsets and RouteInputToChannel makes
                // no links. That is a silent channel, not a failure: every
                // stereo interface (XLR Dock, Wave XLR, MK.2) would otherwise
                // fail the whole build here.
                if (feed.Pairs.Count == 0) continue;
                nextFeeds[ch.Id] = feed;
            }
        }
        catch
        {
            // Roll back only objects created for this candidate graph. Entries
            // reused from the old direct graph must stay connected.
            foreach (PortLink link in nextChainOuts.Values) _pw.Unlink(link);
            foreach (PortLink link in nextSidechains.Values) _pw.Unlink(link);
            foreach ((string key, PortLink link) in nextFeeds)
                if (!_inputFeeds.TryGetValue(key, out PortLink? old) || !ReferenceEquals(old, link))
                    _pw.Unlink(link);
            foreach (FilterHandle chain in nextChains.Values) _pw.StopFilter(chain);
            throw;
        }

        foreach ((string key, PortLink old) in _inputFeeds)
            if (!nextFeeds.TryGetValue(key, out PortLink? keep) || !ReferenceEquals(old, keep))
                _pw.Unlink(old);
        foreach (PortLink old in _chainOuts.Values) _pw.Unlink(old);
        foreach (string key in _chains.Keys.Where(k => _config.Channels.Any(c => c.Id == k && c.InputPair is not null)).ToList())
        {
            _pw.StopFilter(_chains[key]);
            _chains.Remove(key);
        }

        foreach (ChannelDefinition channel in _config.Channels.Where(channel => channel.InputPair is not null))
        {
            string prefix = channel.Id + '\0';
            ReplaceSidechainLinksLocked(channel.Id, nextSidechains.Where(pair =>
                pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary());
        }

        _inputFeeds.Clear();
        foreach ((string key, PortLink feed) in nextFeeds) _inputFeeds[key] = feed;
        _chainOuts.Clear();
        foreach ((string key, PortLink link) in nextChainOuts) _chainOuts[key] = link;
        foreach ((string key, FilterHandle chain) in nextChains) _chains[key] = chain;
        _inputDevice = nextInput;

        // With a chain in the path, a direct input-to-channel link that this
        // daemon does not track (left by an earlier run, or auto-linked by
        // the session manager) would double the unfiltered signal onto the
        // filtered one. Clear such links now that the chain route is live;
        // the chain's own links are between other nodes and untouched.
        foreach (string key in nextChains.Keys)
        {
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == key);
            if (ch is not null) _pw.UnlinkNodes(nextInput, ch.SinkName);
        }
    }

    private void RemoveInputChainsLocked()
    {
        // Input chains only; mix chains are owned by WireMixChainLocked.
        foreach (PortLink link in _chainOuts.Values) _pw.Unlink(link);
        _chainOuts.Clear();
        var inputIds = _config.Channels.Where(channel => channel.InputPair is not null)
            .Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _chains.Keys.Where(inputIds.Contains).ToList())
        {
            _pw.StopFilter(_chains[key]);
            _chains.Remove(key);
        }
        foreach (ChannelDefinition channel in _config.Channels.Where(channel => channel.InputPair is not null))
            ReplaceSidechainLinksLocked(channel.Id, new Dictionary<string, PortLink>());
    }

    private void RemoveMixChainsLocked()
    {
        foreach (PortLink link in _mixTaps.Values) _pw.Unlink(link);
        foreach (PortLink link in _mixPostLinks.Values) _pw.Unlink(link);
        _mixTaps.Clear();
        _mixPostLinks.Clear();
        foreach (string key in _chains.Keys.Where(k => k.StartsWith("mix:", StringComparison.Ordinal)).ToList())
        {
            _pw.StopFilter(_chains[key]);
            _chains.Remove(key);
        }
        foreach (MixDefinition mix in _config.Mixes)
            ReplaceSidechainLinksLocked(MixKey(mix), new Dictionary<string, PortLink>());
    }

    /// <summary>
    /// Sweep healing for built-in DSP and plugin filter chains: a dead holder
    /// process or broken link re-wires the affected path. True when something
    /// changed.
    /// </summary>
    public bool EnsureFilterRoutes()
    {
        lock (_gate)
        {
            if (!_built) return false;
            bool changed = false;
            // Mix chains heal individually; input chains re-wire the whole input path.
            foreach (MixDefinition mix in _config.Mixes)
            {
                string key = MixKey(mix);
                bool retryDue = _pluginRecovery.IsRetryDue(key);
                if (_chains.TryGetValue(key, out FilterHandle? c) && !c.IsAlive)
                {
                    foreach ((string insertId, _) in c.NativeStages.Where(stage => !stage.Host.IsHealthy))
                        _pluginRecovery.RecordFailure(key, insertId, "isolated plug-in host exited or stopped responding");
                    WireMixChainLocked(mix);
                    changed = true;
                }
                else if (retryDue)
                {
                    WireMixChainLocked(mix);
                    changed = true;
                }
            }
            foreach (ChannelDefinition channel in _config.Channels.Where(c => c.InputPair is null))
            {
                bool dead = _chains.TryGetValue(channel.Id, out FilterHandle? chain) && !chain.IsAlive;
                if (dead)
                    foreach ((string insertId, _) in chain!.NativeStages.Where(stage => !stage.Host.IsHealthy))
                        _pluginRecovery.RecordFailure(channel.Id, insertId, "isolated plug-in host exited or stopped responding");
                if (dead || _pluginRecovery.IsRetryDue(channel.Id)
                    || !_appFeeds.TryGetValue(channel.Id, out PortLink? feed) || _pw.EnsureLinks(feed) == LinkHealth.Broken
                    || _appOutputs.TryGetValue(channel.Id, out PortLink? output) && _pw.EnsureLinks(output) == LinkHealth.Broken)
                {
                    WireAppChainLocked(channel);
                    changed = true;
                }
            }
            var inputTargets = _config.Channels.Where(channel => channel.InputPair is not null)
                .Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
            foreach ((string target, FilterHandle chain) in _chains.Where(entry => inputTargets.Contains(entry.Key) && !entry.Value.IsAlive))
                foreach ((string insertId, _) in chain.NativeStages.Where(stage => !stage.Host.IsHealthy))
                    _pluginRecovery.RecordFailure(target, insertId, "isolated plug-in host exited or stopped responding");
            bool inputBroken = _chains.Where(e => inputTargets.Contains(e.Key)).Any(e => !e.Value.IsAlive)
                || _chainOuts.Values.Any(l => _pw.EnsureLinks(l) == LinkHealth.Broken);
            inputBroken |= inputTargets.Any(_pluginRecovery.IsRetryDue);
            if (inputBroken) { WireInputFeedsLocked(); changed = true; }
            return changed;
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
        if (!_auxPortEnabled || !_hardwareOutputRouting) return;
        MixDefinition? aux = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.AuxPort);
        if (aux is null) return;
        // The raw sink is hidden from pickers, so derive it from any Pro
        // pseudo-output's bare name.
        string? proSink = _pw.ListDevices(
                exposeHardwareMonitorOutputs: true, hardwareSinkHint: _inputHint)
            .Where(d => d.Kind == AudioNodeKind.Sink &&
                        d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))
            .Select(d => { int m = d.Name.IndexOf('#'); return m < 0 ? d.Name : d.Name[..m]; })
            .FirstOrDefault();
        if (proSink is null) return;
        (string tapNode, string tapPrefix) = MixTapLocked(aux);
        PortLink route = _pw.RouteTapToOutput(tapNode, tapPrefix, proSink + "#usbaux");
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
        lock (_gate)
        {
            sink = _auxTargetSink;
            if (sink is null || !_built) return;
            foreach (string key in _outputRouteLinks.Keys.Concat(_outputRouteDelays.Keys)
                         .Concat(_outputRouteDelayInputs.Keys).Distinct(StringComparer.Ordinal).ToList())
                TearDownRouteGraphLocked(key);
            if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        }
        _pw.BounceSink(sink);
        lock (_gate)
        {
            EnsureMonitorRoutes();
            RefreshLegacyMonitorOutputsLocked();
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
    private bool _hardwareOutputRouting;
    private int _lowCutHz;                 // 0 = off; software low cut on the first XLR channel
    // Filter-chains by insert key. Input keys ("xlr1", "xlr2") hold a mono
    // chain per hardware input that needs one (the first XLR channel's also
    // carries the soft low cut and ClipGuard); mix keys ("mix:stream") hold
    // a stereo chain spliced between the mix and its consumers.
    private readonly Dictionary<string, FilterHandle> _chains = new();
    private readonly Dictionary<string, PortLink> _chainOuts = new();   // input chains: source half into the channel sink
    private readonly Dictionary<string, PortLink> _mixTaps = new();     // mix key: mix monitor into chain or post sink
    private readonly Dictionary<string, PortLink> _mixPostLinks = new(); // mix key: chain source into the post sink
    private readonly Dictionary<string, PortLink> _appFeeds = new();
    private readonly Dictionary<string, PortLink> _appOutputs = new();
    private readonly Dictionary<string, PortLink> _sidechainLinks = new();

    // Plugin insert chains by key, and why a key's last build fell back to
    // running without its inserts.
    private readonly Dictionary<string, List<InsertDefinition>> _inserts = new();
    private readonly Dictionary<string, string> _insertErrors = new();
    private readonly PluginRecoveryTracker _pluginRecovery = new();

    /// <summary>Insert keys: every channel ID and "mix:&lt;id&gt;" for each output mix.</summary>
    private bool IsInsertChannel(string key) => _config.Channels.Any(c => c.Id == key) || MixForKey(key) is not null;

    private MixDefinition? MixForKey(string key)
        => key.StartsWith("mix:", StringComparison.Ordinal) ? _config.Mixes.FirstOrDefault(m => m.Id == key[4..]) : null;

    private static string MixKey(MixDefinition mix) => $"mix:{mix.Id}";

    private List<InsertDefinition> RunnableInsertsFor(string target)
        => [.. InsertsFor(target).Select(insert => insert.Bypass ||
                _pluginRecovery.CanAttempt(target, insert.Id)
            ? insert
            : insert with { Bypass = true })];

    private void RecordChainFailureLocked(string target,
        IEnumerable<InsertDefinition> attempted, Exception error)
    {
        foreach (InsertDefinition insert in attempted.Where(insert => !insert.Bypass))
            _pluginRecovery.RecordFailure(target, insert.Id, error.Message);
    }

    private void MarkRunningPluginsHealthyLocked(string target, FilterHandle chain)
    {
        foreach ((string insertId, _) in chain.NativeStages)
            _pluginRecovery.MarkHealthy(target, insertId);
    }

    private IReadOnlyList<PluginSidechainSource> SidechainSourcesLocked()
        => [.. _config.Channels.Select(channel => new PluginSidechainSource(
                $"channel:{channel.Id}", channel.Name, channel.InputPair is null ? 2 : 1))
            .Concat(_config.Mixes.Select(mix => new PluginSidechainSource(
                $"mix:{mix.Id}", mix.Name + " mix", 2)))];

    private (string Node, string Prefix, int Channels)? ResolveSidechainSourceLocked(string sourceId)
    {
        if (sourceId.StartsWith("channel:", StringComparison.Ordinal))
        {
            ChannelDefinition? channel = _config.Channels.FirstOrDefault(item =>
                item.Id == sourceId[8..]);
            return channel is null ? null
                : (channel.SinkName, "monitor", channel.InputPair is null ? 2 : 1);
        }
        if (sourceId.StartsWith("mix:", StringComparison.Ordinal))
        {
            MixDefinition? mix = _config.Mixes.FirstOrDefault(item => item.Id == sourceId[4..]);
            if (mix is null) return null;
            (string node, string prefix) = MixTapLocked(mix);
            return (node, prefix, 2);
        }
        return null;
    }

    private Dictionary<string, PortLink> CreateSidechainLinksLocked(string target,
        FilterHandle chain)
    {
        var links = new Dictionary<string, PortLink>(StringComparer.Ordinal);
        try
        {
            foreach ((string insertId, NativePluginHost host) in chain.NativeStages)
            {
                InsertDefinition? insert = InsertsFor(target).FirstOrDefault(item => item.Id == insertId);
                PluginInfo? plugin = insert is null ? null : PluginRegistry.Find(insert);
                if (insert is null || plugin is null) continue;
                foreach ((string busId, string sourceId) in insert.Sidechains)
                {
                    PluginBusInfo bus = plugin.AuxiliaryInputs?.FirstOrDefault(item => item.Id == busId)
                        ?? throw new InvalidOperationException($"plug-in '{plugin.Name}' has no auxiliary bus '{busId}'");
                    (string Node, string Prefix, int Channels)? source = ResolveSidechainSourceLocked(sourceId);
                    if (source is null) throw new InvalidOperationException($"unknown sidechain source '{sourceId}'");
                    string? error = SidechainRouting.Validate(target, sourceId, source.Value.Channels, bus.Channels);
                    if (error is not null) throw new InvalidOperationException(error);
                    if (!busId.StartsWith("aux-", StringComparison.Ordinal)
                        || !int.TryParse(busId.AsSpan(4), out int busIndex) || busIndex < 1)
                        throw new InvalidOperationException($"unsupported auxiliary bus identity '{busId}'");
                    PortLink link = _pw.LinkNodes(source.Value.Node, source.Value.Prefix,
                        host.NodeName, $"sidechain_{busIndex}");
                    if (link.Pairs.Count < bus.Channels)
                        throw new InvalidOperationException($"could not connect sidechain bus '{bus.Name}'");
                    links[$"{target}\0{insertId}\0{busId}"] = link;
                }
            }
            return links;
        }
        catch
        {
            foreach (PortLink link in links.Values) _pw.Unlink(link);
            throw;
        }
    }

    private void ReplaceSidechainLinksLocked(string target,
        IReadOnlyDictionary<string, PortLink> replacement)
    {
        string prefix = target + '\0';
        foreach (string key in _sidechainLinks.Keys.Where(key =>
                     key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _pw.Unlink(_sidechainLinks[key]);
            _sidechainLinks.Remove(key);
        }
        foreach ((string key, PortLink link) in replacement) _sidechainLinks[key] = link;
    }

    /// <summary>
    /// Keep the public application sink stable, process its monitor once, then
    /// feed the existing combine legs. Every output therefore gets the same
    /// processed channel at its own independent send level.
    /// </summary>
    private void WireAppChainLocked(ChannelDefinition channel)
    {
        string key = channel.Id;
        if (_appFeeds.Remove(key, out PortLink? feed)) _pw.Unlink(feed);
        if (_appOutputs.Remove(key, out PortLink? output)) _pw.Unlink(output);
        if (_chains.Remove(key, out FilterHandle? old)) _pw.StopFilter(old);
        _insertErrors.Remove(key);
        List<InsertDefinition> inserts = RunnableInsertsFor(key);
        if (inserts.Any(i => !i.Bypass))
        {
            Dictionary<string, PortLink> candidateSidechains = [];
            try
            {
                FilterHandle chain = _pw.CreateMixChain($"ch_{key}", $"OpenXLR {channel.Name} Inserts", inserts);
                _chains[key] = chain;
                MarkRunningPluginsHealthyLocked(key, chain);
                candidateSidechains = CreateSidechainLinksLocked(key, chain);
                _appFeeds[key] = _pw.LinkNodes(channel.SinkName, "monitor", chain.SinkName, "playback");
                _appOutputs[key] = _pw.LinkNodes(chain.SourceName, "capture", channel.FanOutSinkName, "input");
                if (_appFeeds[key].Pairs.Count < 2 || _appOutputs[key].Pairs.Count < 2)
                    throw new InvalidOperationException("Could not connect the application channel's stereo insert chain.");
                ReplaceSidechainLinksLocked(key, candidateSidechains);
                return;
            }
            catch (Exception ex)
            {
                foreach (PortLink link in candidateSidechains.Values) _pw.Unlink(link);
                RecordChainFailureLocked(key, inserts, ex);
                if (_appFeeds.Remove(key, out PortLink? failedFeed)) _pw.Unlink(failedFeed);
                if (_appOutputs.Remove(key, out PortLink? failedOutput)) _pw.Unlink(failedOutput);
                if (_chains.Remove(key, out FilterHandle? failed)) _pw.StopFilter(failed);
                _insertErrors[key] = ex.Message;
            }
        }
        ReplaceSidechainLinksLocked(key, new Dictionary<string, PortLink>());
        _appFeeds[key] = _pw.LinkNodes(channel.SinkName, "monitor", channel.FanOutSinkName, "input");
        if (_appFeeds[key].Pairs.Count < 2) throw new InvalidOperationException($"Could not connect channel '{key}' to its sends.");
    }

    /// <summary>Where a mix's consumers should read from: its insert chain when one runs, else its own monitor.</summary>
    private (string Node, string Prefix) MixTapLocked(MixDefinition mix)
        => _chains.TryGetValue(MixKey(mix), out FilterHandle? chain) ? (chain.SourceName, "capture") : (mix.SinkName, "monitor");

    /// <summary>
    /// (Re)build one mix's insert chain and re-point everything that reads
    /// the mix: the post sink behind a virtual mic, the monitor routes, or
    /// the aux route. Without inserts the mix feeds them directly.
    /// </summary>
    private void WireMixChainLocked(MixDefinition mix)
    {
        string key = MixKey(mix);
        if (_mixTaps.Remove(key, out PortLink? tap)) _pw.Unlink(tap);
        if (_mixPostLinks.Remove(key, out PortLink? post)) _pw.Unlink(post);
        if (_chains.Remove(key, out FilterHandle? old)) _pw.StopFilter(old);
        _insertErrors.Remove(key);

        List<InsertDefinition> inserts = RunnableInsertsFor(key);
        bool anyInsert = inserts.Any(i => !i.Bypass && PluginRegistry.Find(i) is { } p && p.AudioIns >= 2 && p.AudioOuts >= 2);
        if (anyInsert)
        {
            Dictionary<string, PortLink> candidateSidechains = [];
            try
            {
                FilterHandle chain = _pw.CreateMixChain(mix.Id, $"OpenXLR {mix.Name} Inserts", inserts);
                _chains[key] = chain;
                MarkRunningPluginsHealthyLocked(key, chain);
                candidateSidechains = CreateSidechainLinksLocked(key, chain);
                _mixTaps[key] = _pw.LinkNodes(mix.SinkName, "monitor", chain.SinkName, "playback");
                if (_mixTaps[key].Pairs.Count < 2)
                    throw new InvalidOperationException("Could not connect the output mix's stereo insert chain.");
                ReplaceSidechainLinksLocked(key, candidateSidechains);
            }
            catch (Exception ex)
            {
                foreach (PortLink link in candidateSidechains.Values) _pw.Unlink(link);
                RecordChainFailureLocked(key, inserts, ex);
                if (_mixTaps.Remove(key, out PortLink? failedTap)) _pw.Unlink(failedTap);
                if (_chains.Remove(key, out FilterHandle? failed)) _pw.StopFilter(failed);
                _insertErrors[key] = ex.Message;   // the mix keeps flowing without its inserts
                ReplaceSidechainLinksLocked(key, new Dictionary<string, PortLink>());
            }
        }
        else ReplaceSidechainLinksLocked(key, new Dictionary<string, PortLink>());
        (string node, string prefix) = MixTapLocked(mix);
        switch (mix.Kind)
        {
            case MixKind.VirtualMic:
                // The virtual mic reads the post sink, so its identity never
                // changes when a chain comes or goes.
                _mixPostLinks[key] = _pw.LinkNodes(node, prefix, mix.PostSinkName, "playback");
                break;
            case MixKind.AuxPort:
                WireAuxRouteLocked();
                break;
        }
        RewireOutputRoutesForMixLocked(mix.Id);
    }

    private List<InsertDefinition> InsertsFor(string channelId)
        => _inserts.TryGetValue(channelId, out List<InsertDefinition>? l) ? l : [];

    private void RestoreInsertsLocked(IReadOnlyDictionary<string, List<InsertDefinition>> saved)
    {
        _inserts.Clear();
        IReadOnlyDictionary<string, PluginSidechainSource> sources = SidechainSourcesLocked()
            .ToDictionary(source => source.Id, StringComparer.Ordinal);
        foreach ((string target, List<InsertDefinition> list) in saved)
        {
            if (!IsInsertChannel(target)) continue;
            _inserts[target] = [.. list.Select(insert => insert with
            {
                Params = new Dictionary<string, double>(insert.Params),
                Sidechains = insert.Sidechains.Where(route =>
                {
                    if (!sources.TryGetValue(route.Value, out PluginSidechainSource? source)) return false;
                    PluginInfo? plugin = PluginRegistry.Find(insert);
                    PluginBusInfo? bus = plugin?.AuxiliaryInputs?
                        .FirstOrDefault(candidate => candidate.Id == route.Key);
                    return plugin is null
                        ? SidechainRouting.Validate(target, source.Id, source.Channels, source.Channels) is null
                        : bus is not null && SidechainRouting.Validate(target, source.Id,
                            source.Channels, bus.Channels) is null;
                }).ToDictionary(route => route.Key, route => route.Value),
            })];
        }
    }

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

    // Software ClipGuard: a post-ADC hard limiter at -3 dB in the mic filter
    // chain, for devices whose ClipGuard runs host-side in the vendor app. It
    // limits the recorded signal but cannot undo clipping in the preamp/ADC.
    private bool _softClipGuard;
    private bool _clipGuardApplicable = true;

    /// <summary>Whether the software ClipGuard is enabled.</summary>
    public bool SoftClipGuard { get { lock (_gate) return _softClipGuard; } }

    public void SetSoftClipGuard(bool on)
    {
        lock (_gate)
        {
            if (_softClipGuard == on) return;
            if (on && _clipGuardApplicable)
            {
                DspFeatureAvailability support = _pw.GetSoftwareClipGuardAvailability();
                if (!support.Available) throw new InvalidOperationException(support.Error);
            }
            bool previous = _softClipGuard;
            _softClipGuard = on;
            try
            {
                if (_built) WireInputFeedsLocked();
            }
            catch
            {
                // WireInputFeedsLocked swaps only after the candidate graph is
                // complete, so restoring the requested state is enough: the
                // previous audible graph is still intact.
                _softClipGuard = previous;
                throw;
            }
        }
    }

    /// <summary>False while the active device has the hardware ClipGuard.</summary>
    public void SetClipGuardApplicable(bool applicable)
    {
        lock (_gate)
        {
            if (_clipGuardApplicable == applicable) return;
            _clipGuardApplicable = applicable;
            if (applicable && _softClipGuard && !_pw.GetSoftwareClipGuardAvailability().Available)
                _softClipGuard = false;
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

    // --- plugin inserts ---

    /// <summary>Replace a channel's insert chain (order matters); rewires when built.</summary>
    public void SetInserts(string channel, IReadOnlyList<InsertDefinition> inserts)
    {
        lock (_gate)
        {
            if (!IsInsertChannel(channel)) throw new InvalidOperationException($"unknown insert target '{channel}'");
            if (inserts.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != inserts.Count)
                throw new InvalidOperationException("insert IDs must be unique within a chain");
            int width = channel is "xlr1" or "xlr2" ? 1 : 2;
            foreach (InsertDefinition insert in inserts)
            {
                if (string.IsNullOrWhiteSpace(insert.Id) || insert.Kind is not ("lv2" or "vst3"))
                    throw new InvalidOperationException("every insert needs a nonempty ID and kind 'lv2' or 'vst3'");
                PluginInfo plugin = PluginRegistry.Find(insert)
                    ?? throw new InvalidOperationException($"plugin not installed: {insert.Plugin}");
                if (plugin.ScanStatus != "ready")
                    throw new InvalidOperationException($"plugin is not ready: {plugin.ScanError ?? plugin.ScanStatus}");
                if (plugin.AudioIns < width || plugin.AudioOuts < width)
                    throw new InvalidOperationException($"plugin '{plugin.Name}' does not fit a {width}-channel path");
                foreach ((string symbol, double value) in insert.Params)
                {
                    PluginParam? parameter = plugin.Params.FirstOrDefault(p => p.Symbol == symbol);
                    if (parameter is null || !double.IsFinite(value) || value < parameter.Min || value > parameter.Max)
                        throw new InvalidOperationException($"invalid plugin control '{symbol}'");
                }
                foreach ((string busId, string sourceId) in insert.Sidechains)
                {
                    PluginBusInfo bus = plugin.AuxiliaryInputs?.FirstOrDefault(item => item.Id == busId)
                        ?? throw new InvalidOperationException($"plugin '{plugin.Name}' has no auxiliary bus '{busId}'");
                    PluginSidechainSource source = SidechainSourcesLocked().FirstOrDefault(item => item.Id == sourceId)
                        ?? throw new InvalidOperationException($"unknown sidechain source '{sourceId}'");
                    string? sidechainError = SidechainRouting.Validate(channel, source.Id,
                        source.Channels, bus.Channels);
                    if (sidechainError is not null) throw new InvalidOperationException(sidechainError);
                }
            }
            _inserts[channel] = [.. inserts.Select(i => i with
            {
                Params = new Dictionary<string, double>(i.Params),
                Sidechains = new Dictionary<string, string>(i.Sidechains),
            })];
            _pluginRecovery.Retain(channel, inserts.Select(insert => insert.Id));
            if (_built) RewireInsertKeyLocked(channel);
        }
    }

    /// <summary>
    /// Retry one failed isolated host. Quarantined crash loops require the
    /// caller to explicitly clear quarantine; ordinary backoff can be retried
    /// early without clearing its failure history.
    /// </summary>
    public void RetryInsertHost(string channel, string insertId, bool clearQuarantine)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list)
                || list.All(insert => insert.Id != insertId))
                throw new InvalidOperationException($"unknown insert '{insertId}'");
            if (!_pluginRecovery.Retry(channel, insertId, clearQuarantine))
                throw new InvalidOperationException("insert has no retryable failure, or is quarantined");
            if (_built) RewireInsertKeyLocked(channel);
        }
    }

    /// <summary>Copy a chain for preset persistence without exposing mutable mixer dictionaries.</summary>
    public IReadOnlyList<InsertDefinition> GetInserts(string channel)
    {
        lock (_gate)
        {
            if (!IsInsertChannel(channel)) throw new InvalidOperationException($"unknown insert target '{channel}'");
            return [.. InsertsFor(channel).Select(insert => insert with
            {
                Params = new Dictionary<string, double>(insert.Params),
                Sidechains = new Dictionary<string, string>(insert.Sidechains),
            })];
        }
    }

    /// <summary>Apply a reusable plug-in preset to an existing compatible slot.</summary>
    public void ApplyPluginPreset(string channel, string insertId, InsertDefinition preset)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list))
                throw new InvalidOperationException($"unknown insert target '{channel}'");
            int index = list.FindIndex(insert => insert.Id == insertId);
            if (index < 0) throw new InvalidOperationException($"unknown insert '{insertId}'");
            InsertDefinition current = list[index];
            if (current.Kind != preset.Kind || current.Plugin != preset.Plugin)
                throw new InvalidOperationException("plugin preset belongs to a different plug-in");
            list[index] = preset with
            {
                Id = current.Id,
                Label = current.Label,
                Params = new Dictionary<string, double>(preset.Params),
                Sidechains = new Dictionary<string, string>(preset.Sidechains),
            };
            if (_built) RewireInsertKeyLocked(channel);
        }
    }

    /// <summary>Bypass or re-enable one insert; rewires when built.</summary>
    public void SetInsertBypass(string channel, string insertId, bool bypass)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list))
                throw new InvalidOperationException($"unknown insert target '{channel}'");
            int idx = list.FindIndex(i => i.Id == insertId);
            if (idx < 0) throw new InvalidOperationException($"unknown insert '{insertId}'");
            if (list[idx].Bypass == bypass) return;
            list[idx] = list[idx] with { Bypass = bypass };
            if (_built) RewireInsertKeyLocked(channel);
        }
    }

    private void RewireInsertKeyLocked(string key)
    {
        if (MixForKey(key) is MixDefinition mix) WireMixChainLocked(mix);
        else if (_config.Channels.FirstOrDefault(c => c.Id == key && c.InputPair is null) is { } channel) WireAppChainLocked(channel);
        else if (IsInsertChannel(key)) WireInputFeedsLocked();
    }

    /// <summary>Open the vendor UI on the actual audio-processing instance.</summary>
    public void ShowInsertUi(string channel, string insertId)
    {
        lock (_gate)
        {
            NativePluginHost? host = _chains.GetValueOrDefault(channel)?.NativeStages.FirstOrDefault(s => s.Id == insertId).Host;
            if (host is null) throw new InvalidOperationException("Enable the plugin first; its native host is not running.");
            host.ShowUi();
        }
    }

    /// <summary>Persist native UI edits without feeding them back into the host.</summary>
    public bool SyncPluginControls()
    {
        lock (_gate)
        {
            bool changed = false;
            foreach ((string channel, FilterHandle chain) in _chains)
                foreach ((string id, NativePluginHost host) in chain.NativeStages)
                {
                    bool instanceChanged = false;
                    foreach ((string symbol, double value) in host.DrainChanges())
                    {
                        InsertDefinition? insert = InsertsFor(channel).FirstOrDefault(i => i.Id == id);
                        PluginParam? parameter = insert is null ? null : PluginRegistry.Find(insert)?.Params.FirstOrDefault(p => p.Symbol == symbol);
                        if (parameter is null || !double.IsFinite(value)) continue;
                        insert!.Params[symbol] = Math.Clamp(value, parameter.Min, parameter.Max);
                        instanceChanged = true;
                        changed = true;
                    }
                    if (instanceChanged && _inserts.TryGetValue(channel, out List<InsertDefinition>? inserts))
                    {
                        int index = inserts.FindIndex(insert => insert.Id == id);
                        if (index >= 0 && PluginRegistry.Find(inserts[index])?.SupportsState == true)
                        {
                            try { inserts[index] = inserts[index] with { State = host.CaptureState() }; }
                            catch (InvalidOperationException) { /* transparent parameters still persist */ }
                        }
                    }
                }
            return changed;
        }
    }

    /// <summary>
    /// Set one control of an insert. Applied live to the running chain when
    /// possible (no dropout); a chain that cannot take it is rebuilt.
    /// </summary>
    public void SetInsertParam(string channel, string insertId, string symbol, double value)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list))
                throw new InvalidOperationException($"unknown insert target '{channel}'");
            int idx = list.FindIndex(i => i.Id == insertId);
            if (idx < 0) throw new InvalidOperationException($"unknown insert '{insertId}'");
            if (!double.IsFinite(value)) throw new InvalidOperationException("plugin control must be finite");
            PluginParam? parameter = PluginRegistry.Find(list[idx])?.Params.FirstOrDefault(p => p.Symbol == symbol);
            if (parameter is null) throw new InvalidOperationException($"unknown plugin control '{symbol}'");
            value = Math.Clamp(parameter.Integer ? Math.Round(value) : value, parameter.Min, parameter.Max);
            list[idx].Params[symbol] = value;
            if (!_built || list[idx].Bypass || _insertErrors.ContainsKey(channel)
                || !_chains.TryGetValue(channel, out FilterHandle? chain)) return;
            // The chain names LV2 stages i0, i1, ... in the order of the
            // non-bypassed, loadable inserts, so find this insert's stage index.
            int k = 0;
            for (int j = 0; j < idx; j++)
                if (!list[j].Bypass && PluginRegistry.Find(list[j]) is not null) k++;
            try { _pw.SetFilterControl(chain, $"i{k}:{symbol}", value); }
            catch (InvalidOperationException) { RewireInsertKeyLocked(channel); }
        }
    }

    /// <summary>Insert chains with live status, for the state push.</summary>
    private Dictionary<string, IReadOnlyList<InsertStatus>> InsertStatusLocked()
    {
        var result = new Dictionary<string, IReadOnlyList<InsertStatus>>();
        foreach ((string channel, List<InsertDefinition> list) in _inserts)
        {
            result[channel] = [.. list.Select(insert =>
            {
                PluginInfo? plugin = PluginRegistry.Find(insert);
                string? error = plugin is not { ScanStatus: "ready" }
                    ? plugin?.ScanError ?? "plugin not installed"
                    : !insert.Bypass && _insertErrors.TryGetValue(channel, out string? chainError)
                        ? chainError : null;
                NativePluginHost? host = _chains.GetValueOrDefault(channel)?.NativeStages
                    .FirstOrDefault(stage => stage.Id == insert.Id).Host;
                PluginRecoveryStatus? recovery = _pluginRecovery.Get(channel, insert.Id);
                string status = insert.Bypass ? "bypassed"
                    : recovery?.Quarantined == true ? "quarantined"
                    : recovery?.RetryAt is not null ? "recovering"
                    : plugin?.ScanStatus ?? "missing";
                error ??= recovery is { RetryAt: not null } or { Quarantined: true }
                    ? recovery.LastError : null;
                return new InsertStatus(insert, error, host?.Meters,
                    host?.LatencySamples ?? plugin?.LatencySamples ?? 0,
                    status, recovery);
            })];
        }
        return result;
    }

    /// <summary>
    /// Re-evaluate saved chains after an isolated catalogue scan. This is a
    /// control-thread operation; affected paths retain their normal fail-open
    /// rollback if a newly discovered plug-in cannot instantiate.
    /// </summary>
    public void RefreshPluginCatalog()
    {
        lock (_gate)
        {
            if (!_built) return;
            foreach (string key in _inserts.Keys.Where(key => _inserts[key].Any(insert => !insert.Bypass)).ToList())
                RewireInsertKeyLocked(key);
        }
    }

    /// <summary>
    /// Name fragment of the interface whose capture should feed the input
    /// channels (the daemon's active device). A change re-wires the feeds.
    /// </summary>
    public void SetInputDeviceHint(string? hint, bool hardwareOutputRouting = false)
    {
        lock (_gate)
        {
            if (_inputHint == hint && _hardwareOutputRouting == hardwareOutputRouting) return;
            bool routingChanged = _hardwareOutputRouting != hardwareOutputRouting;
            _inputHint = hint;
            _hardwareOutputRouting = hardwareOutputRouting;
            if (_built)
            {
                WireInputFeedsLocked();
                if (routingChanged) WireAuxRouteLocked();
            }
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
            if (!_built) return false;
            // No feeds at all (device absent at build), or feeds whose source
            // node has since vanished (a card profile change renames every
            // node under it): both mean re-resolve the input and re-wire.
            bool broken = _inputFeeds.Count == 0
                || _inputFeeds.Values.Any(f => _pw.EnsureLinks(f) == LinkHealth.Broken);
            if (!broken) return false;
            WireInputFeedsLocked();
            return _inputFeeds.Count > 0;
        }
    }

    /// <summary>Every sink and source the user can pick, real or virtual.</summary>
    public IReadOnlyList<AudioNode> ListDevices()
        => _pw.ListDevices(_hardwareOutputRouting, _inputHint);

    /// <summary>Close and reopen an output device's stream (see adapter).</summary>
    public void BounceOutput(string sinkName) => _pw.BounceSink(sinkName);

    /// <summary>Current user choices, for persisting.</summary>
    public MixerSettings ExportSettings()
    {
        lock (_gate)
        {
            return new MixerSettings
            {
                UserChannels = [.. _config.Channels.Where(c => c.InputPair is null)
                    .Select(c => new UserChannelDefinition(c.Id, c.Name))],
                UserMixes = [.. _config.Mixes.Where(m => m.Kind == MixKind.VirtualMic)
                    .Select(m => new UserMixDefinition(m.Id, m.Name))],
                MixVolumes = new Dictionary<string, double>(_mixVolume),
                MixMuted = [.. _mixMuted],
                Levels = new Dictionary<string, double>(_levels),
                ChannelMuted = [.. _muted],
                MonitorOutputs = [.. _monitorOutputs],
                MonitoredMixId = _monitoredMixId,
                OutputRoutes = [.. _outputRoutes],
                AppOverrides = new Dictionary<string, string>(Matcher.Overrides),
                KnownApps = [.. _apps.Values.Select(a => new SavedApp(a.Identity, a.Label, a.ChannelId))],
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                Inserts = _inserts.ToDictionary(e => e.Key, e => e.Value.ToList()),
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
            foreach (string mixId in s.MixMuted)
                if (_mixVolume.ContainsKey(mixId)) _mixMuted.Add(mixId);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            _muted.Clear();
            foreach (string cell in s.ChannelMuted)
                if (_cells.Contains(cell)) _muted.Add(cell);

            var validAppChannels = _config.Channels.Where(c => c.InputPair is null).Select(c => c.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string fallbackChannel = validAppChannels.FirstOrDefault()
                ?? _config.Channels.FirstOrDefault()?.Id ?? "system";
            Matcher.ClearOverrides();
            foreach ((string identity, string channelId) in s.AppOverrides)
                Matcher.SetOverride(StreamMatcher.MigrateIdentity(Sanitize(identity)),
                    validAppChannels.Contains(channelId) ? channelId : fallbackChannel);

            // A live reconfiguration keeps the registry in memory. Move any
            // app whose channel was removed onto the safe application channel.
            foreach ((string identity, StreamAssignment app) in _apps.ToList())
            {
                string channel = Matcher.Overrides.TryGetValue(identity, out string? assigned)
                    ? assigned
                    : validAppChannels.Contains(app.ChannelId) ? app.ChannelId : fallbackChannel;
                _apps[identity] = app with { ChannelId = channel };
            }

            // Remembered apps come back inactive until a stream appears.
            // Identities saved before the "(deleted)" fix are migrated here so
            // an app does not appear twice after its binary was updated.
            foreach (SavedApp app in s.KnownApps)
            {
                string identity = StreamMatcher.MigrateIdentity(Sanitize(app.Identity));
                if (PipeWireAdapter.IsPlumbingIdentity(identity)) continue;   // pre-filter leftovers
                string channel = validAppChannels.Contains(app.ChannelId) ? app.ChannelId : fallbackChannel;
                if (!_apps.ContainsKey(identity))
                    _apps[identity] = new StreamAssignment(0, 0, Sanitize(app.Label), identity, channel) { Active = false, Running = false };
                else
                    _apps[identity] = _apps[identity] with { ChannelId = channel };
            }

            static string Sanitize(string v) => v.EndsWith(" (deleted)", StringComparison.Ordinal) ? v[..^10] : v;

            foreach (MixDefinition mix in _config.Mixes) ReapplyMixLocked(mix.Id);

            _monitoredMixId = ResolveMonitoredMixId(s.MonitoredMixId);
            if (s.OutputRoutes is not null)
                RestoreOutputRoutesLocked(s.OutputRoutes);
            else
            {
                // Schema-1 migration: the selected mix and runtime output
                // names become stable-ID matrix routes on the first save.
                IReadOnlyList<string> savedOutputs = s.MonitorOutputs is { Count: > 0 }
                    ? s.MonitorOutputs
                    : s.MonitorOutput is not null ? [s.MonitorOutput] : [];
                var liveNames = RoutingDestinationsLocked().Where(d => d.NodeName is not null)
                    .Select(d => d.NodeName!).ToHashSet(StringComparer.Ordinal);
                SetMonitorOutputsLocked([.. savedOutputs.Where(liveNames.Contains)]);
            }
            _enforcedSink = s.EnforcedDefaultSink;
            _enforcedSource = s.EnforcedDefaultSource;

            // Migration: before the Aux mix existed, "USB Aux Out" was a
            // monitor destination; carry that intent over once.
            _auxPortEnabled = s.AuxPortEnabled
                ?? (s.MonitorOutputs.Any(o => o.EndsWith("#usbaux", StringComparison.Ordinal)) ||
                    (s.MonitorOutput?.EndsWith("#usbaux", StringComparison.Ordinal) ?? false));
            WireAuxRouteLocked();

            bool rewireInputs = false;
            bool rewireMixes = false;
            if (s.LowCutHz is 80 or 120 && _lowCutHz != s.LowCutHz)
            {
                _lowCutHz = s.LowCutHz;
                rewireInputs = true;
            }
            if (s.SoftClipGuard && !_softClipGuard)
            {
                // A stale saved preference must not prevent the entire mixer
                // from starting on a machine where the optional LADSPA bundle
                // is absent. Keep the plain/low-cut route and expose the
                // dependency error in MixerState instead.
                if (_pw.GetSoftwareClipGuardAvailability().Available)
                {
                    _softClipGuard = true;
                    rewireInputs = true;
                }
            }
            RestoreInsertsLocked(s.Inserts);
            rewireInputs = true;
            rewireMixes = true;
            if (rewireInputs) WireInputFeedsLocked();
            if (rewireMixes)
                foreach (MixDefinition mix in _config.Mixes) WireMixChainLocked(mix);
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
                MonitoredMixId = _monitoredMixId,
                OutputRoutes = [.. _outputRoutes],
                AuxPortEnabled = _auxPortEnabled,
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                Inserts = _inserts.ToDictionary(e => e.Key, e => e.Value.ToList()),
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
            foreach (string mixId in s.MixMuted)
                if (_mixVolume.ContainsKey(mixId)) _mixMuted.Add(mixId);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            _muted.Clear();
            foreach (string cell in s.ChannelMuted)
                if (_cells.Contains(cell)) _muted.Add(cell);

            foreach (MixDefinition mix in _config.Mixes) ReapplyMixLocked(mix.Id);

            bool monitoredMixChanged = false;
            if (s.MonitoredMixId is not null)
            {
                string selected = ResolveMonitoredMixId(s.MonitoredMixId);
                monitoredMixChanged = selected != _monitoredMixId;
                _monitoredMixId = selected;
            }

            // An explicit matrix replaces all routes. Older profiles preserve
            // the current matrix, except for their legacy monitor fields.
            if (s.OutputRoutes is not null)
                RestoreOutputRoutesLocked(s.OutputRoutes);
            else if (s.MonitorOutputs is not null)
                SetMonitorOutputsLocked(s.MonitorOutputs);
            else if (monitoredMixChanged)
                SetMonitorOutputsLocked([.. _monitorOutputs]);
            _auxPortEnabled = s.AuxPortEnabled;
            WireAuxRouteLocked();

            bool rewireInputs = false;
            bool rewireMixes = false;
            if (s.LowCutHz is int hz && hz is 0 or 80 or 120 && _lowCutHz != hz)
            {
                _lowCutHz = hz;
                rewireInputs = true;
            }
            if (s.SoftClipGuard is bool scg && _softClipGuard != scg)
            {
                if (!scg || _pw.GetSoftwareClipGuardAvailability().Available)
                {
                    _softClipGuard = scg;
                    rewireInputs = true;
                }
            }
            if (s.Inserts is not null)
            {
                RestoreInsertsLocked(s.Inserts);
                rewireInputs = true;
                rewireMixes = true;
            }
            if (rewireInputs) WireInputFeedsLocked();
            if (rewireMixes)
                foreach (MixDefinition mix in _config.Mixes) WireMixChainLocked(mix);
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

    /// <summary>Runtime node names of all available matrix destinations.</summary>
    public IReadOnlyList<string> RoutedOutputNames
    {
        get
        {
            lock (_gate)
            {
                IReadOnlyDictionary<string, RoutingDestination> destinations = RoutingDestinationsLocked()
                    .Where(d => d.NodeName is not null).ToDictionary(d => d.Id);
                return [.. _outputRoutes.Select(r => destinations.GetValueOrDefault(r.DestinationId)?.NodeName)
                    .Where(n => n is not null).Cast<string>().Distinct(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>The mix whose post-insert signal is heard on the monitor outputs.</summary>
    public string MonitoredMixId { get { lock (_gate) return _monitoredMixId; } }

    /// <summary>
    /// Listen to any existing output mix on the selected physical outputs.
    /// The mix's master, mute, and insert chain stay in the monitored path.
    /// </summary>
    public void SetMonitoredMix(string mixId)
    {
        lock (_gate)
        {
            if (!_config.Mixes.Any(m => m.Id == mixId))
                throw new InvalidOperationException($"unknown monitored mix '{mixId}'");
            if (_monitoredMixId == mixId) return;
            _monitoredMixId = mixId;
            RefreshLegacyMonitorOutputsLocked();
        }
    }

    private string ResolveMonitoredMixId(string? requested)
    {
        if (requested is not null && _config.Mixes.Any(m => m.Id == requested)) return requested;
        return _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor)?.Id
            ?? _config.Mixes.FirstOrDefault()?.Id
            ?? "monitor";
    }

    /// <summary>
    /// Send the listened mix to one output device (or none). Kept for clients
    /// that think in a single monitor destination.
    /// </summary>
    public void SetMonitorOutput(string? sinkName)
        => SetMonitorOutputs(sinkName is null ? [] : [sinkName]);

    /// <summary>
    /// Send the listened mix to any set of output devices at once. Any sink
    /// works, virtual ones included; an empty list disconnects the monitor.
    /// </summary>
    public void SetMonitorOutputs(IReadOnlyList<string> sinkNames)
    {
        lock (_gate) { if (_built) SetMonitorOutputsLocked(sinkNames); }
    }

    private void SetMonitorOutputsLocked(IReadOnlyList<string> sinkNames)
    {
        IReadOnlyList<RoutingDestination> destinations = RoutingDestinationsLocked();
        var desired = new List<OutputRouteDefinition>();
        foreach (string name in sinkNames.Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.Ordinal))
        {
            // USB Aux has a structural compatibility command of its own.
            if (name.EndsWith("#usbaux", StringComparison.Ordinal)) continue;
            RoutingDestination? destination = destinations.FirstOrDefault(d => d.NodeName == name);
            if (destination is null)
                throw new InvalidOperationException($"unknown output device '{name}'");
            desired.Add(new OutputRouteDefinition(_monitoredMixId, destination.Id,
                ProcessingStage.MixProcessed, destination.Name));
        }
        ReplaceRoutesForMixLocked(_monitoredMixId, desired);
        RefreshLegacyMonitorOutputsLocked();
    }

    /// <summary>Every compatible live sink plus remembered disconnected columns.</summary>
    private IReadOnlyList<RoutingDestination> RoutingDestinationsLocked()
    {
        var result = _pw.ListDevices(_hardwareOutputRouting, _inputHint)
            .Where(node => node.Kind == AudioNodeKind.Sink && !node.IsOwn)
            .Select(node => new RoutingDestination(
                node.Id, node.Description, node.Name, Available: true,
                node.IsPhysical, Compatible: true,
                [ProcessingStage.MixProcessed]))
            .ToList();
        foreach (OutputRouteDefinition route in _outputRoutes)
        {
            if (result.Any(d => d.Id == route.DestinationId)) continue;
            result.Add(new RoutingDestination(route.DestinationId,
                route.DestinationLabel ?? "Disconnected output", null,
                Available: false, IsPhysical: false, Compatible: false, [],
                "The output is currently disconnected."));
        }
        return result.OrderByDescending(d => d.Available)
            .ThenByDescending(d => d.IsPhysical)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Add, change, or remove one routing-matrix cell. All routes converging on
    /// the destination are rebuilt as one group so their latency compensation
    /// remains coherent. Desired state changes only after the new group works;
    /// on failure the previous group is restored.
    /// </summary>
    public void SetOutputRoute(string mixId, string destinationId, bool enabled,
        ProcessingStage stage = ProcessingStage.MixProcessed)
    {
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer not built");
            OutputRouteDefinition? previous = _outputRoutes.FirstOrDefault(r =>
                r.MixId == mixId && r.DestinationId == destinationId);
            if (!enabled)
            {
                if (previous is null) return;
                ApplyDestinationRoutesLocked(destinationId, [.. _outputRoutes.Where(route =>
                    route.DestinationId == destinationId && route.MixId != mixId)]);
                RefreshLegacyMonitorOutputsLocked();
                return;
            }

            RoutingDestination? destination = RoutingDestinationsLocked()
                .FirstOrDefault(d => d.Id == destinationId);
            var candidate = new OutputRouteDefinition(mixId, destinationId, stage, destination?.Name);
            string? error = SignalRouting.Validate(candidate,
                _config.Mixes.Select(m => m.Id).ToArray(), RoutingDestinationsLocked(), _outputRoutes);
            if (error is not null) throw new InvalidOperationException(error);
            OutputRouteDefinition[] desired =
            [
                .. _outputRoutes.Where(route => route.DestinationId == destinationId
                    && route.MixId != mixId),
                candidate,
            ];
            if (previous == candidate && DestinationGraphHealthyLocked(desired)) return;
            ApplyDestinationRoutesLocked(destinationId, desired);
            RefreshLegacyMonitorOutputsLocked();
        }
    }

    private int MixLatencySamplesLocked(string mixId)
    {
        long total = 0;
        FilterHandle? chain = _chains.GetValueOrDefault($"mix:{mixId}");
        if (chain is not null)
        {
            total += (long)chain.Stages.Count * _graphQuantumSamples;
            foreach ((_, NativePluginHost host) in chain.NativeStages)
                total += Math.Clamp(host.LatencySamples, 0, CompensationDelayHost.MaximumSamples);
        }
        return (int)Math.Min(total, CompensationDelayHost.MaximumSamples);
    }

    private IReadOnlyDictionary<string, int> CompensationPlanLocked(
        IEnumerable<OutputRouteDefinition> routes)
        => LatencyCompensation.Calculate(routes.Select(route =>
            (OutputRouteKey(route.MixId, route.DestinationId),
             MixLatencySamplesLocked(route.MixId))));

    private bool DestinationGraphHealthyLocked(IReadOnlyList<OutputRouteDefinition> routes)
    {
        IReadOnlyDictionary<string, int> plan = CompensationPlanLocked(routes);
        bool usesCompensationStage = plan.Values.Any(samples => samples > 0);
        foreach (OutputRouteDefinition route in routes)
        {
            string key = OutputRouteKey(route.MixId, route.DestinationId);
            if (!_outputRouteLinks.TryGetValue(key, out PortLink? output)
                || output.Pairs.Count == 0 || _pw.EnsureLinks(output) == LinkHealth.Broken
                || !_routeCompensationSamples.TryGetValue(key, out int actual)
                || actual != plan[key]) return false;
            if (!usesCompensationStage)
            {
                if (_outputRouteDelays.ContainsKey(key) || _outputRouteDelayInputs.ContainsKey(key))
                    return false;
                continue;
            }
            if (!_outputRouteDelays.TryGetValue(key, out FilterHandle? delay) || !delay.IsAlive
                || !_outputRouteDelayInputs.TryGetValue(key, out PortLink? input)
                || input.Pairs.Count == 0 || _pw.EnsureLinks(input) == LinkHealth.Broken)
                return false;
        }
        return true;
    }

    private void TearDownRouteGraphLocked(string key)
    {
        if (_outputRouteLinks.Remove(key, out PortLink? output)) _pw.Unlink(output);
        if (_outputRouteDelayInputs.Remove(key, out PortLink? input)) _pw.Unlink(input);
        if (_outputRouteDelays.Remove(key, out FilterHandle? delay)) _pw.StopFilter(delay);
        _routeCompensationSamples.Remove(key);
    }

    private void TearDownDestinationGraphLocked(string destinationId)
    {
        string suffix = "|" + destinationId;
        foreach (string key in _outputRouteLinks.Keys
                     .Concat(_outputRouteDelays.Keys)
                     .Concat(_outputRouteDelayInputs.Keys)
                     .Where(key => key.EndsWith(suffix, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal).ToList())
            TearDownRouteGraphLocked(key);
    }

    private void BuildDestinationGraphLocked(string destinationId,
        IReadOnlyList<OutputRouteDefinition> routes)
    {
        RoutingDestination? destination = RoutingDestinationsLocked().FirstOrDefault(item =>
            item.Id == destinationId && item.Available);
        if (destination?.NodeName is null) return;
        IReadOnlyDictionary<string, int> plan = CompensationPlanLocked(routes);
        // Every branch gets the same helper stage whenever alignment is
        // required. A PipeWire process node itself costs one graph cycle; a
        // zero-delay helper on the slowest branch keeps that fixed cost equal.
        bool usesCompensationStage = plan.Values.Any(samples => samples > 0);
        var built = new List<string>();
        try
        {
            foreach (OutputRouteDefinition route in routes)
            {
                string key = OutputRouteKey(route.MixId, route.DestinationId);
                MixDefinition mix = _config.Mixes.Single(item => item.Id == route.MixId);
                (string tapNode, string tapPrefix) = MixTapForStageLocked(mix, route.Stage);
                int compensation = plan[key];
                if (usesCompensationStage)
                {
                    string delayId = SignalRouting.StableDestinationId(key)[7..23];
                    FilterHandle delay = _pw.CreateCompensationDelay(delayId, 2, compensation);
                    _outputRouteDelays[key] = delay;
                    PortLink into = _pw.LinkNodes(tapNode, tapPrefix, delay.SinkName, "playback");
                    if (into.Pairs.Count < 2) throw new InvalidOperationException("could not feed latency compensation");
                    _outputRouteDelayInputs[key] = into;
                    tapNode = delay.SourceName;
                    tapPrefix = "capture";
                }
                PortLink output = _pw.RouteTapToOutput(tapNode, tapPrefix, destination.NodeName);
                if (output.Pairs.Count == 0 || _pw.EnsureLinks(output) == LinkHealth.Broken)
                    throw new InvalidOperationException($"could not activate route to '{destination.Name}'");
                _outputRouteLinks[key] = output;
                _routeCompensationSamples[key] = compensation;
                built.Add(key);
            }
        }
        catch
        {
            foreach (string key in built) TearDownRouteGraphLocked(key);
            // A failure can happen after the delay was recorded but before its
            // final output link was added.
            foreach (OutputRouteDefinition route in routes)
                TearDownRouteGraphLocked(OutputRouteKey(route.MixId, route.DestinationId));
            throw;
        }
    }

    private void ApplyDestinationRoutesLocked(string destinationId,
        IReadOnlyList<OutputRouteDefinition> desired)
    {
        List<OutputRouteDefinition> previous = [.. _outputRoutes.Where(route =>
            route.DestinationId == destinationId)];
        TearDownDestinationGraphLocked(destinationId);
        try
        {
            BuildDestinationGraphLocked(destinationId, desired);
            _outputRoutes.RemoveAll(route => route.DestinationId == destinationId);
            _outputRoutes.AddRange(desired);
        }
        catch
        {
            try { BuildDestinationGraphLocked(destinationId, previous); }
            catch { /* original error remains authoritative; sweep can heal the old intent */ }
            throw;
        }
    }

    private (string Node, string Prefix) MixTapForStageLocked(MixDefinition mix, ProcessingStage stage)
        => stage switch
        {
            ProcessingStage.MixProcessed => MixTapLocked(mix),
            _ => throw new InvalidOperationException($"processing stage '{stage}' is not available for mix routes"),
        };

    private void ReplaceRoutesForMixLocked(string mixId, IReadOnlyList<OutputRouteDefinition> routes)
    {
        var previous = _outputRoutes.Where(r => r.MixId == mixId).ToList();
        var added = new List<OutputRouteDefinition>();
        try
        {
            foreach (OutputRouteDefinition route in routes)
            {
                SetOutputRoute(route.MixId, route.DestinationId, true, route.Stage);
                added.Add(route);
            }
            foreach (OutputRouteDefinition route in previous.Where(old => !routes.Any(next =>
                         next.DestinationId == old.DestinationId)))
                SetOutputRoute(route.MixId, route.DestinationId, false, route.Stage);
        }
        catch
        {
            foreach (OutputRouteDefinition route in added.Where(a => !previous.Any(p =>
                         p.DestinationId == a.DestinationId)))
                SetOutputRoute(route.MixId, route.DestinationId, false, route.Stage);
            throw;
        }
    }

    private void RefreshLegacyMonitorOutputsLocked()
    {
        IReadOnlyDictionary<string, RoutingDestination> destinations = RoutingDestinationsLocked()
            .Where(d => d.NodeName is not null).ToDictionary(d => d.Id);
        _monitorOutputs.Clear();
        foreach (OutputRouteDefinition route in _outputRoutes.Where(r => r.MixId == _monitoredMixId))
            if (destinations.TryGetValue(route.DestinationId, out RoutingDestination? destination))
                _monitorOutputs.Add(destination.NodeName!);
    }

    /// <summary>
    /// Repoint only one mix after its insert tap changes. Other mixes and
    /// destinations are untouched, and desired routes remain intact when a
    /// destination is temporarily absent.
    /// </summary>
    private void RewireOutputRoutesForMixLocked(string mixId)
    {
        foreach (string destinationId in _outputRoutes.Where(route => route.MixId == mixId)
                     .Select(route => route.DestinationId).Distinct(StringComparer.Ordinal).ToList())
        {
            OutputRouteDefinition[] desired = [.. _outputRoutes.Where(route =>
                route.DestinationId == destinationId)];
            try { ApplyDestinationRoutesLocked(destinationId, desired); }
            catch (InvalidOperationException) { /* desired state remains; the health sweep retries */ }
        }
        RefreshLegacyMonitorOutputsLocked();
    }

    /// <summary>
    /// Verify the listened mix's device links and repair what died: a USB
    /// output that re-enumerates (suspend, reset, profile change) takes its
    /// node and every link on it along, and a device absent at wiring time
    /// got no links at all. Runs every sweep; true when something changed.
    /// </summary>
    public bool EnsureMonitorRoutes()
    {
        lock (_gate)
        {
            if (!_built || _outputRoutes.Count == 0) return false;
            bool changed = false;
            IReadOnlyDictionary<string, RoutingDestination> destinations = RoutingDestinationsLocked()
                .Where(d => d.Available).ToDictionary(d => d.Id);
            foreach (IGrouping<string, OutputRouteDefinition> group in _outputRoutes
                         .GroupBy(route => route.DestinationId, StringComparer.Ordinal).ToList())
            {
                OutputRouteDefinition[] routes = [.. group];
                if (!destinations.ContainsKey(group.Key))
                {
                    bool hadGraph = routes.Any(route => _outputRouteLinks.ContainsKey(
                        OutputRouteKey(route.MixId, route.DestinationId)));
                    TearDownDestinationGraphLocked(group.Key);
                    changed |= hadGraph;
                    continue;
                }
                if (DestinationGraphHealthyLocked(routes)) continue;
                try { ApplyDestinationRoutesLocked(group.Key, routes); changed = true; }
                catch (InvalidOperationException) { /* retain desired state and try on a later sweep */ }
            }
            if (changed) RefreshLegacyMonitorOutputsLocked();
            return changed;
        }
    }

    /// <summary>Level of one channel in one mix (0..1).</summary>
    public void SetLevel(string channelId, string mixId, double level)
    {
        lock (_gate)
        {
            string cell = Cell(channelId, mixId);
            if (!_cells.Contains(cell)) throw new InvalidOperationException($"unknown channel/mix send '{cell}'");
            if (!double.IsFinite(level)) throw new InvalidOperationException("level must be finite");
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
            if (!_cells.Contains(cell)) throw new InvalidOperationException($"unknown channel/mix send '{cell}'");
            if (muted) _muted.Add(cell); else _muted.Remove(cell);
            ApplyCellLocked(channelId, mixId);
        }
    }

    /// <summary>Master level for a mix, scaling every channel feeding it.</summary>
    public void SetMixVolume(string mixId, double volume)
    {
        lock (_gate)
        {
            if (!_config.Mixes.Any(m => m.Id == mixId)) throw new InvalidOperationException($"unknown mix '{mixId}'");
            if (!double.IsFinite(volume)) throw new InvalidOperationException("volume must be finite");
            _mixVolume[mixId] = Math.Clamp(volume, 0.0, 1.0);
            ReapplyMixLocked(mixId);
        }
    }

    public void SetMixMuted(string mixId, bool muted)
    {
        lock (_gate)
        {
            if (!_config.Mixes.Any(m => m.Id == mixId)) throw new InvalidOperationException($"unknown mix '{mixId}'");
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
    /// on a timer by the daemon. Returns true when anything changed. A new stream
    /// is tracked only after Pulse publishes its requested destination; this lets
    /// the next sweep retry a move that raced the stream's initial binding.
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
                                        ?? _config.Channels.FirstOrDefault(c => c.InputPair is null)
                                        ?? _config.Channels.FirstOrDefault();
                if (ch is null) continue;

                try
                {
                    if (!_pw.IsStreamOnSink(s.Serial, ch.SinkName))
                    {
                        _pw.MoveStreamToSink(s.Serial, ch.SinkName);
                        if (!_pw.IsStreamOnSink(s.Serial, ch.SinkName)) continue;
                    }
                    // The mixer owns muting (sends, masters) from here on; a
                    // per-stream mute remembered by stream-restore has no
                    // control anywhere in OpenXLR and just reads as silence.
                    _pw.SetSinkInputMuted(s.Serial, false);
                }
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
                    string matched = Matcher.Match(client);
                    string channel = _config.Channels.Any(c => c.Id == matched && c.InputPair is null)
                        ? matched
                        : _config.Channels.FirstOrDefault(c => c.InputPair is null)?.Id
                          ?? _config.Channels.FirstOrDefault()?.Id ?? "system";
                    _apps[identity] = new StreamAssignment(0, 0, client.Label, identity,
                        channel)
                    { Active = false, Running = true };
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
                        Active = activeNow,
                        Running = runningNow,
                        Id = activeNow ? app.Id : 0,
                        Serial = activeNow ? app.Serial : 0,
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
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId && c.InputPair is null);
            if (ch is null || string.IsNullOrWhiteSpace(identity)) return;
            Matcher.SetOverride(identity, channelId);

            foreach ((int id, StreamAssignment placed) in _streams.ToList())
                if (placed.Identity == identity)
                {
                    try { _pw.MoveStreamToSink(placed.Serial, ch.SinkName); }
                    catch (InvalidOperationException) { /* the sweep retries */ }
                    _streams.Remove(id);
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
                _streams.Remove(streamId); // next sweep confirms the published target
                return;
            }
            _pw.MoveStreamToSink(streamId, ch.SinkName);
        }
    }

    public MixerState Snapshot()
    {
        lock (_gate)
        {
            DspFeatureAvailability clipGuard = _pw.GetSoftwareClipGuardAvailability();
            bool canDeleteApplicationChannel = _config.Channels.Count(c => c.InputPair is null) > 1;
            return new MixerState
            {
                Mixes = [.. _config.Mixes.Select(m => new MixStatus(
                    m.Id, m.Name,
                    _mixVolume.GetValueOrDefault(m.Id, 1.0),
                    _mixMuted.Contains(m.Id),
                    m.Kind == MixKind.Monitor,
                    m.Kind == MixKind.VirtualMic,
                    m.Kind == MixKind.AuxPort,
                    m.Kind == MixKind.VirtualMic))],
                Channels = [.. _config.Channels.Select(c => new ChannelStatus(
                    c.Id, c.Name,
                    _config.Mixes.ToDictionary(m => m.Id, m => _levels.GetValueOrDefault(Cell(c.Id, m.Id), 0.0)),
                    [.. _config.Mixes.Where(m => _muted.Contains(Cell(c.Id, m.Id))).Select(m => m.Id)],
                    c.InputPair is not null,
                    c.InputPair is null,
                    c.InputPair is null && canDeleteApplicationChannel))],
                MonitorOutput = _monitorOutputs.FirstOrDefault(),
                MonitorOutputs = [.. _monitorOutputs],
                MonitoredMixId = _monitoredMixId,
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                SoftClipGuardAvailable = clipGuard.Available,
                SoftClipGuardError = clipGuard.Error,
                Inserts = InsertStatusLocked(),
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                RoutingDestinations = RoutingDestinationsLocked(),
                OutputRoutes = OutputRouteStatusLocked(),
                SidechainSources = SidechainSourcesLocked(),
                Streams = [.. _apps.Values
                    .OrderByDescending(a => a.Active).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)],
            };
        }
    }

    private void RestoreOutputRoutesLocked(IEnumerable<OutputRouteDefinition> routes)
    {
        foreach (string key in _outputRouteLinks.Keys.Concat(_outputRouteDelays.Keys)
                     .Concat(_outputRouteDelayInputs.Keys).Distinct(StringComparer.Ordinal).ToList())
            TearDownRouteGraphLocked(key);
        _outputRoutes.Clear();
        IReadOnlyDictionary<string, RoutingDestination> live = RoutingDestinationsLocked()
            .Where(d => d.Available).ToDictionary(d => d.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (OutputRouteDefinition route in routes)
        {
            if (!_config.Mixes.Any(m => m.Id == route.MixId)) continue;
            if (route.Stage != ProcessingStage.MixProcessed) continue;
            string key = OutputRouteKey(route.MixId, route.DestinationId);
            if (!seen.Add(key)) continue;
            if (live.ContainsKey(route.DestinationId))
            {
                SetOutputRoute(route.MixId, route.DestinationId, true, route.Stage);
                continue;
            }
            // Retain unplugged intent by its stable ID. It is not linked until
            // a device with that exact identity returns.
            _outputRoutes.Add(route with
            {
                DestinationLabel = string.IsNullOrWhiteSpace(route.DestinationLabel)
                    ? "Disconnected output" : route.DestinationLabel.Trim(),
            });
        }
        RefreshLegacyMonitorOutputsLocked();
    }

    private IReadOnlyList<OutputRouteStatus> OutputRouteStatusLocked()
    {
        IReadOnlyDictionary<string, RoutingDestination> destinations = RoutingDestinationsLocked()
            .ToDictionary(d => d.Id);
        return [.. _outputRoutes.Select(route =>
        {
            bool available = destinations.TryGetValue(route.DestinationId, out RoutingDestination? destination)
                && destination.Available;
            bool active = _outputRouteLinks.TryGetValue(
                OutputRouteKey(route.MixId, route.DestinationId), out PortLink? link) && link.Pairs.Count > 0;
            string key = OutputRouteKey(route.MixId, route.DestinationId);
            int sourceLatency = MixLatencySamplesLocked(route.MixId);
            int compensation = _routeCompensationSamples.GetValueOrDefault(key);
            return new OutputRouteStatus(route.MixId, route.DestinationId, route.Stage,
                active, available, available && !active ? "Route is not connected." :
                available ? null : "Output is disconnected.", sourceLatency, compensation);
        })];
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
        foreach (string key in _outputRouteLinks.Keys.Concat(_outputRouteDelays.Keys)
                     .Concat(_outputRouteDelayInputs.Keys).Distinct(StringComparer.Ordinal).ToList())
            TearDownRouteGraphLocked(key);
        _outputRoutes.Clear();
        _monitorOutputs.Clear();
        foreach (PortLink sidechain in _sidechainLinks.Values) _pw.Unlink(sidechain);
        _sidechainLinks.Clear();
        if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        foreach (PortLink feed in _inputFeeds.Values) _pw.Unlink(feed);
        _inputFeeds.Clear();
        RemoveInputChainsLocked();
        RemoveMixChainsLocked();
        foreach (PortLink link in _appFeeds.Values.Concat(_appOutputs.Values)) _pw.Unlink(link);
        _appFeeds.Clear();
        _appOutputs.Clear();
        foreach (FilterHandle chain in _chains.Values) _pw.StopFilter(chain);
        _chains.Clear();
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
