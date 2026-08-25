using System.Diagnostics;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The single seam over PipeWire. Everything the mixer does to the audio graph
/// goes through here, using the primitives verified on hardware:
///   - null sink            : pactl load-module module-null-sink
///   - virtual capture device: pactl load-module module-remap-source
///   - fader                : pw-loopback (channel monitor -> mix) whose playback
///                            node volume is set live with wpctl
///   - discovery            : pw-dump (JSON graph)
/// Loopback volume is set explicitly after spawn, because the channelVolumes launch
/// property does not reliably take effect.
/// </summary>
public sealed class PipeWireAdapter
{
    private readonly List<uint> _modules = [];
    private readonly List<Process> _loopbacks = [];

    /// <summary>
    /// pactl joins its arguments into one module-argument string and re-splits on
    /// whitespace, so a description containing spaces is truncated unless each
    /// space is backslash-escaped *inside* the quoted value. Verified on
    /// PipeWire 1.6: node.description="OpenXLR\ Monitor" survives, while plain
    /// quoting, single quotes, and a bare backslash all lose everything after
    /// the first space. Without this, every OpenXLR node shows as just
    /// "OpenXLR" in pavucontrol, OBS, and Discord.
    /// </summary>
    private static string PropValue(string value) => '"' + value.Replace(" ", "\\ ") + '"';

    /// <summary>Load a null sink; returns its module id for later unload.</summary>
    public uint CreateNullSink(string nodeName, string description)
    {
        // suspend-on-idle must be off: an idle channel sink would otherwise be
        // suspended by PipeWire and drop the first moment of audio (or all of it)
        // when an application starts playing into it again.
        string outp = Run("pactl",
            "load-module", "module-null-sink",
            $"sink_name={nodeName}",
            "media.class=Audio/Sink",
            // priority.session far below any hardware sink, so WirePlumber never
            // auto-switches the system default to one of our internal sinks
            // (which silently swallows the user's desktop audio).
            $"sink_properties=node.description={PropValue(description)}" +
            " node.suspend-on-idle=false priority.session=100");
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>
    /// A remap sink is a filter attached to a master sink: it is clocked by its
    /// master, so audio written to it always flows (no loopback process, no
    /// separate clock island). One remap per channel-mix cell carries that
    /// cell's fader volume.
    /// </summary>
    public uint CreateRemapSink(string nodeName, string masterSink, string description)
    {
        string outp = Run("pactl",
            "load-module", "module-remap-sink",
            $"sink_name={nodeName}",
            $"master={masterSink}",
            $"sink_properties=node.description={PropValue(description)}" +
            " priority.session=90");
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>
    /// A combine sink duplicates its input into several slave sinks. One per
    /// channel: applications play into it and every mix receives the audio
    /// through that channel's remap cells.
    /// </summary>
    public uint CreateCombineSink(string nodeName, IEnumerable<string> slaveSinks, string description)
    {
        string outp = Run("pactl",
            "load-module", "module-combine-sink",
            $"sink_name={nodeName}",
            $"slaves={string.Join(',', slaveSinks)}",
            // suspend-on-idle=false keeps the combine's monitor source running;
            // a suspended monitor makes the channel's level meter read silence
            // even while audio flows through the sink.
            $"sink_properties=node.description={PropValue(description)}" +
            " priority.session=100 node.suspend-on-idle=false");
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>Set a sink's volume.</summary>
    public void SetSinkVolume(string sinkName, double volume)
        => Run("pactl", "set-sink-volume", sinkName,
            $"{(int)Math.Round(Math.Clamp(volume, 0, 1) * 100)}%");

    /// <summary>Mute or unmute a sink.</summary>
    public void SetSinkMuted(string sinkName, bool muted)
        => Run("pactl", "set-sink-mute", sinkName, muted ? "1" : "0");

    /// <summary>Set one sink-input's volume (used for the combine fader legs).</summary>
    public void SetSinkInputVolume(int index, double volume)
        => Run("pactl", "set-sink-input-volume", index.ToString(),
            $"{(int)Math.Round(Math.Clamp(volume, 0, 1) * 100)}%");

    /// <summary>Mute or unmute one sink-input.</summary>
    public void SetSinkInputMuted(int index, bool muted)
        => Run("pactl", "set-sink-input-mute", index.ToString(), muted ? "1" : "0");

    /// <summary>
    /// The internal streams of a combine sink module, keyed by the sink each
    /// feeds. These streams ARE the channel's faders: one per mix, each with its
    /// own volume and mute.
    /// </summary>
    public IReadOnlyDictionary<string, int> FindCombineLegs(uint combineModule)
    {
        var sinkNames = new Dictionary<string, string>();  // index -> name
        foreach (string line in Run("pactl", "list", "sinks", "short").Split('\n'))
        {
            string[] parts = line.Split('\t');
            if (parts.Length >= 2) sinkNames[parts[0]] = parts[1];
        }

        var legs = new Dictionary<string, int>();
        string listing = Run("pactl", "list", "sink-inputs");
        foreach (string block in listing.Split("Sink Input #").Skip(1))
        {
            int nl = block.IndexOf('\n');
            if (nl < 0 || !int.TryParse(block[..nl].Trim(), out int index)) continue;
            if (!block.Contains($"Owner Module: {combineModule}\n") &&
                !block.Contains($"Owner Module: {combineModule}\r")) continue;
            var m = System.Text.RegularExpressions.Regex.Match(block, @"Sink: (\d+)");
            if (m.Success && sinkNames.TryGetValue(m.Groups[1].Value, out string? name))
                legs[name] = index;
        }
        return legs;
    }

    /// <summary>Volume of a sink as 0..1, parsed from pactl (first channel).</summary>
    public double? GetSinkVolume(string sinkName)
        => ParseVolumePercent(TryRun("pactl", "get-sink-volume", sinkName));

    /// <summary>Volume of a source as 0..1.</summary>
    public double? GetSourceVolume(string sourceName)
        => ParseVolumePercent(TryRun("pactl", "get-source-volume", sourceName));

    public void SetSourceVolume(string sourceName, double volume)
        => Run("pactl", "set-source-volume", sourceName,
            $"{(int)Math.Round(Math.Clamp(volume, 0, 1) * 100)}%");

    private string? TryRun(string exe, params string[] args)
    {
        try { return Run(exe, args); }
        catch (InvalidOperationException) { return null; }
    }

    private static double? ParseVolumePercent(string? pactlOutput)
    {
        if (pactlOutput is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(pactlOutput, @"(\d+)%");
        return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value) / 100.0, 0, 1.5) : null;
    }

    /// <summary>The capture device applications record from by default.</summary>
    public void SetDefaultSource(string sourceName)
        => Run("pactl", "set-default-source", sourceName);

    /// <summary>The playback device applications use by default.</summary>
    public void SetDefaultSink(string sinkName)
        => Run("pactl", "set-default-sink", sinkName);

    /// <summary>Current default playback device, or null.</summary>
    public string? GetDefaultSink()
    {
        try { return Run("pactl", "get-default-sink").Trim(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Current default capture device, or null.</summary>
    public string? GetDefaultSource()
    {
        try { return Run("pactl", "get-default-source").Trim(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Publish a sink's monitor as a cleanly-named capture device.</summary>
    public uint CreateVirtualMic(string sourceName, string masterMonitor, string description)
    {
        // The master monitor source registers asynchronously after its sink is
        // created; loading the remap before it exists fails with EINVAL. Seen
        // only on a freshly restarted PipeWire, so wait for it briefly.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string sources = TryRun("pactl", "list", "sources", "short") ?? "";
            if (sources.Contains(masterMonitor, StringComparison.Ordinal)) break;
            Thread.Sleep(200);
        }
        string outp = Run("pactl",
            "load-module", "module-remap-source",
            $"source_name={sourceName}",
            $"master={masterMonitor}",
            // Both properties: apps read one or the other depending on the API.
            // Low priority.session so WirePlumber never promotes a virtual mic
            // to system default capture on its own.
            $"source_properties=device.description={PropValue(description)}" +
            $" node.description={PropValue(description)} priority.session=100");
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>
    /// Spawn a fader: carries <paramref name="fromSink"/>'s monitor into
    /// <paramref name="toSink"/>. The returned playback node name is what
    /// <see cref="SetLoopbackVolume"/> addresses. Set
    /// <paramref name="fromIsSource"/> when the origin is a real capture device
    /// (a microphone) rather than a sink whose monitor is being tapped.
    /// </summary>
    public LoopbackHandle CreateLoopback(string id, string fromSink, string toSink, double volume,
        bool fromIsSource = false)
    {
        string capName = $"OpenXLR_lb_{id}_cap";
        string playName = $"OpenXLR_lb_{id}_play";
        var psi = new ProcessStartInfo("pw-loopback")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Deliberately NOT node.passive: a passive node does not drive the graph,
        // and with every hop passive (channel -> mix -> output) nothing pulls the
        // audio through, so the chain goes silent and idle sinks get suspended.
        // Verified by ear: the same audio passes through a non-passive loopback
        // and does not through a passive one.
        psi.ArgumentList.Add("--capture-props=" +
            $"node.name={capName} target.object={fromSink}" +
            (fromIsSource ? "" : " stream.capture.sink=true"));
        psi.ArgumentList.Add("--playback-props=" +
            $"node.name={playName} target.object={toSink}");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start pw-loopback");
        _loopbacks.Add(p);

        var handle = new LoopbackHandle(id, capName, playName, p);
        // Wait for the node to appear, then apply the fader level.
        if (WaitForNode(playName, TimeSpan.FromSeconds(3)))
            SetLoopbackVolume(handle, volume);
        return handle;
    }

    /// <summary>Set a fader level live (0.0 removes the source from that mix).</summary>
    public void SetLoopbackVolume(LoopbackHandle lb, double volume)
    {
        int? nodeId = FindNodeId(lb.PlaybackNodeName);
        if (nodeId is null) return;
        Run("wpctl", "set-volume", nodeId.Value.ToString(),
            volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Route an application's stream onto a channel sink. Takes the stream's
    /// object.serial (the PulseAudio sink-input id), not the PipeWire node id.
    /// </summary>
    public void MoveStreamToSink(int streamSerial, string sinkName)
        => Run("pactl", "move-sink-input", streamSerial.ToString(), sinkName);

    /// <summary>
    /// Connect two nodes with direct port links (FL to FL, FR to FR). Unlike a
    /// loopback there is no process and no clock bridging: the linked island is
    /// driven by the hardware sink's clock, which is what actually makes audio
    /// flow. Loopbacks from a null sink to a hardware sink stall on this system
    /// (audio for about a second, then silence), verified by ear; direct links
    /// are the standard PipeWire answer for this exact routing.
    /// </summary>
    public PortLink LinkNodes(string fromNode, string fromPortPrefix, string toNode, string toPortPrefix,
        int toPairOffset = 0)
    {
        // Port names vary by device (FL/FR on most nodes, AUX0/AUX1 on
        // multichannel interfaces like the Wave XLR Pro), so discover the real
        // ports rather than assuming, then pair them in order. A mono source
        // into a stereo sink gets its one port linked to both inputs.
        List<string> outs = ListPorts(fromNode, fromPortPrefix, output: true);
        List<string> ins = ListPorts(toNode, toPortPrefix, output: false);
        if (toPairOffset > 0 && ins.Count > toPairOffset * 2)
            ins = [.. ins.Skip(toPairOffset * 2).Take(2)];
        else if (ins.Count > 2 && toPairOffset == 0 && toNode.Contains("multichannel", StringComparison.Ordinal))
            ins = [.. ins.Take(2)];

        var pairs = new List<(string From, string To)>();
        for (int i = 0; i < ins.Count && (outs.Count > 0); i++)
        {
            string from = outs[Math.Min(i, outs.Count - 1)];
            string to = ins[i];
            try { Run("pw-link", from, to); pairs.Add((from, to)); }
            catch (InvalidOperationException) { /* racing a disappearing port */ }
        }
        return new PortLink(pairs);
    }

    /// <summary>Ports of a node whose name starts with a prefix, in pw-link order.</summary>
    private List<string> ListPorts(string node, string prefix, bool output)
    {
        var ports = new List<string>();
        string listing = Run("pw-link", output ? "-o" : "-i");
        foreach (string line in listing.Split('\n'))
        {
            string name = line.Trim();
            if (name.StartsWith($"{node}:{prefix}", StringComparison.Ordinal))
                ports.Add(name);
        }
        return ports;
    }

    /// <summary>Remove a set of port links made by <see cref="LinkNodes"/>.</summary>
    public void Unlink(PortLink link)
    {
        foreach ((string from, string to) in link.Pairs)
        {
            try { Run("pw-link", "-d", from, to); }
            catch (InvalidOperationException) { /* already gone */ }
        }
    }

    /// <summary>
    /// Point a mix's sink at an output device. A target of the form
    /// "sink#phonesN" addresses channel pair N of a multichannel sink (the Wave
    /// XLR Pro's two headphone outputs), linking to playback ports 2(N-1) and
    /// 2(N-1)+1 instead of the first pair.
    /// </summary>
    public PortLink RouteMixToOutput(string mixSink, string outputSink)
    {
        int pair = 0;
        int marker = outputSink.IndexOf("#phones", StringComparison.Ordinal);
        if (marker >= 0)
        {
            if (int.TryParse(outputSink[(marker + 7)..], out int n) && n >= 1) pair = n - 1;
            outputSink = outputSink[..marker];
        }
        return LinkNodes(mixSink, "monitor", outputSink, "playback", pair);
    }

    /// <summary>Feed a capture device (microphone) into a channel sink.</summary>
    public PortLink RouteInputToChannel(string sourceName, string channelSink)
        => LinkNodes(sourceName, "capture", channelSink, "playback");

    /// <summary>Stop one loopback (used when re-pointing a device selection).</summary>
    public void StopLoopback(LoopbackHandle lb)
    {
        try { if (!lb.Process.HasExited) { lb.Process.Kill(entireProcessTree: true); lb.Process.WaitForExit(2000); } }
        catch (Exception) { /* already gone */ }
        _loopbacks.Remove(lb.Process);
        lb.Process.Dispose();
    }

    /// <summary>PipeWire node id for a node.name, or null if absent.</summary>
    public int? FindNodeId(string nodeName)
    {
        foreach (var (id, name, _) in DumpNodes())
            if (name == nodeName) return id;
        return null;
    }

    /// <summary>
    /// Every sink (output) and source (input) in the graph, real or virtual.
    /// PipeWire makes no distinction, so a null sink, a loopback, or another
    /// app's virtual device is selectable exactly like a physical card.
    /// Monitor sources are excluded: they are the tap side of a sink, not a
    /// device a user would pick as a microphone.
    /// </summary>
    public IReadOnlyList<AudioNode> ListDevices()
    {
        var found = new List<AudioNode>();
        string json = Run("pw-dump");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return found; }
        using (doc)
        {
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
            {
                if (!o.TryGetProperty("type", out JsonElement t) ||
                    !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
                if (!o.TryGetProperty("info", out JsonElement info) ||
                    !info.TryGetProperty("props", out JsonElement props)) continue;

                string? name = props.TryGetProperty("node.name", out JsonElement n) ? n.GetString() : null;
                if (name is null) continue;
                string mc = props.TryGetProperty("media.class", out JsonElement m) ? m.GetString() ?? "" : "";

                bool isSink = mc == "Audio/Sink";
                bool isSource = mc is "Audio/Source" or "Audio/Source/Virtual";
                if (!isSink && !isSource) continue;
                if (isSource && name.EndsWith(".monitor", StringComparison.Ordinal)) continue;

                string desc = props.TryGetProperty("node.description", out JsonElement d)
                    ? d.GetString() ?? name : name;
                // Real hardware carries device.api (alsa, bluez5, ...); any
                // software-created source or sink does not.
                bool physical = props.TryGetProperty("device.api", out JsonElement api) &&
                                !string.IsNullOrEmpty(api.GetString());
                found.Add(new AudioNode(name, desc, isSink ? AudioNodeKind.Sink : AudioNodeKind.Source,
                    name.StartsWith("OpenXLR", StringComparison.Ordinal), physical));
            }
        }

        // NOTE: the Pro's headphone jacks are NOT reachable through any of its
        // 17 UAC2 playback channels (verified by a full channel sweep with the
        // volume registers at maximum, plus crossfade and mode-block attempts,
        // all silent by ear). The amps are enabled by device state not yet
        // mapped; until a targeted Wave Link startup capture identifies it, no
        // Phones pseudo-devices are advertised because they would route audio
        // into silence. The #phones routing support in RouteMixToOutput stays
        // for when the enable is found.
        return found;
    }

    /// <summary>
    /// Every application playback stream (what PulseAudio calls a sink-input),
    /// with the identity fields the matcher needs. OpenXLR's own loopbacks are
    /// excluded: they are plumbing, not applications.
    /// </summary>
    public IReadOnlyList<AudioStream> ListStreams()
    {
        var found = new List<AudioStream>();
        string json = Run("pw-dump");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return found; }
        using (doc)
        {
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
            {
                if (!o.TryGetProperty("type", out JsonElement t) ||
                    !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
                if (!o.TryGetProperty("info", out JsonElement info) ||
                    !info.TryGetProperty("props", out JsonElement props)) continue;

                string mc = props.TryGetProperty("media.class", out JsonElement m) ? m.GetString() ?? "" : "";
                if (mc != "Stream/Output/Audio") continue;

                // Exclude the mixer's own plumbing. Filter modules (combine and
                // remap sinks, loopbacks) run internal streams named things like
                // "output.OpenXLR_ch_game"; moving those rewires the mixer
                // itself. Real applications never carry node.link-group.
                if (props.TryGetProperty("node.link-group", out _)) continue;

                string? nodeName = props.TryGetProperty("node.name", out JsonElement nn) ? nn.GetString() : null;
                if (nodeName is not null && nodeName.Contains("OpenXLR", StringComparison.Ordinal)) continue;

                string? mediaName = props.TryGetProperty("media.name", out JsonElement mn) ? mn.GetString() : null;
                if (mediaName is not null && mediaName.StartsWith("Simultaneous output", StringComparison.Ordinal)) continue;

                // object.serial is what PulseAudio exposes as the sink-input
                // id, and pactl move-sink-input addresses streams by that, not
                // by the PipeWire node id.
                int serial = props.TryGetProperty("object.serial", out JsonElement os) &&
                             os.TryGetInt32(out int sv) ? sv : o.GetProperty("id").GetInt32();
                found.Add(new AudioStream(
                    o.GetProperty("id").GetInt32(),
                    Str(props, "application.name"),
                    Str(props, "application.process.binary"),
                    Str(props, "media.name")) { Serial = serial });
            }
        }
        return found;

        static string? Str(JsonElement props, string key)
            => props.TryGetProperty(key, out JsonElement v) ? v.GetString() : null;
    }

    /// <summary>All audio nodes as (id, node.name, media.class).</summary>
    public IEnumerable<(int Id, string Name, string MediaClass)> DumpNodes()
    {
        string json = Run("pw-dump");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
            {
                if (!o.TryGetProperty("type", out JsonElement t) ||
                    !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
                if (!o.TryGetProperty("info", out JsonElement info) ||
                    !info.TryGetProperty("props", out JsonElement props)) continue;
                string? name = props.TryGetProperty("node.name", out JsonElement n) ? n.GetString() : null;
                if (name is null) continue;
                string mc = props.TryGetProperty("media.class", out JsonElement m) ? m.GetString() ?? "" : "";
                yield return (o.GetProperty("id").GetInt32(), name, mc);
            }
        }
    }

    private bool WaitForNode(string nodeName, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (FindNodeId(nodeName) is not null) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Remove everything this adapter created, in reverse order.</summary>
    public void TearDown()
    {
        foreach (Process p in _loopbacks)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } }
            catch (Exception) { /* already gone */ }
            p.Dispose();
        }
        _loopbacks.Clear();

        for (int i = _modules.Count - 1; i >= 0; i--)
        {
            try { Run("pactl", "unload-module", _modules[i].ToString()); }
            catch (Exception) { /* already unloaded */ }
        }
        _modules.Clear();
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} failed: {stderr.Trim()}");
        return stdout;
    }
}

/// <summary>A running fader (one channel's send into one mix).</summary>
public sealed record LoopbackHandle(string Id, string CaptureNodeName, string PlaybackNodeName, Process Process);

/// <summary>A set of direct port links between two nodes.</summary>
public sealed record PortLink(IReadOnlyList<(string From, string To)> Pairs);

public enum AudioNodeKind { Sink, Source }

/// <summary>
/// A selectable audio device. PipeWire does not distinguish real hardware from
/// virtual nodes, so both appear here; <paramref name="IsOwn"/> marks the nodes
/// OpenXLR itself created, and <paramref name="IsPhysical"/> marks real
/// hardware (device.api present), letting pickers filter to actual devices.
/// </summary>
public sealed record AudioNode(string Name, string Description, AudioNodeKind Kind, bool IsOwn,
    bool IsPhysical = false);
