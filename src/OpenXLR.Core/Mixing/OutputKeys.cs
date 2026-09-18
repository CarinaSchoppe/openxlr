namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    /// <summary>Adjust a named output, or the current desktop default, in desktop percentage points.</summary>
    public void AdjustOutputVolume(string? device, double delta)
    {
        if (!double.IsFinite(delta) || delta is < -.5 or > .5)
            throw new InvalidOperationException("output volume step must be between -0.5 and 0.5");
        lock (_gate)
        {
            string sink = ResolveOutputKeyLocked(device);
            // A key may arrive before the first sweep after selecting outputs.
            // Establish that selection's baseline so follow-mode copies this
            // explicit change to the other selected outputs as well.
            if (_enforcedSink == FollowMonitorOutput && _outputVolume is null)
                SyncDeviceVolumes();
            double current = _pw.GetSinkVolume(sink) ?? throw new InvalidOperationException("output volume is unavailable");
            double volume = Math.Clamp(current + delta, 0, PipeWireAdapter.MaxSinkVolume);
            MixDefinition? mix = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor && m.SinkName == sink);
            if (mix is not null) SetMixVolume(mix.Id, volume);
            else _pw.SetSinkVolume(sink, volume);
            SyncDeviceVolumes(); // retain the existing linked-monitor/follow-default behavior
        }
    }

    /// <summary>Read mute from the audio server, never from a stale UI value.</summary>
    public void ToggleOutputMute(string? device)
    {
        lock (_gate)
        {
            string sink = ResolveOutputKeyLocked(device);
            MixDefinition? mix = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor && m.SinkName == sink);
            if (mix is null) _pw.ToggleSinkMuted(sink);
            // Use the regular monitor setter so state and graph acknowledgement
            // follow the same path as a click in the mixer, even when registry
            // events have not caught up with pipewire-pulse yet.
            else SetMixMuted(mix.Id, !_pw.GetSinkMuted(sink));
        }
    }

    /// <summary>Change the enforced desktop sink, keeping capture defaults and mixer feeds intact.</summary>
    public void SetMainOutput(string device)
    {
        lock (_gate)
        {
            string sink = ResolveOutputKeyLocked(device == FollowMonitorOutput
                ? ResolveDefaultSink(device, _monitorOutputs) ?? throw new InvalidOperationException("no monitor output is selected")
                : device);
            _pw.SetDefaultSink(sink); // acknowledge only after the audio server accepted this sink
            _enforcedSink = device == FollowMonitorOutput ? FollowMonitorOutput : sink;
        }
    }

    private string ResolveOutputKeyLocked(string? requested)
    {
        if (!_built) throw new InvalidOperationException("mixer is not built");
        string sink = requested ?? _pw.GetDefaultSink() ?? throw new InvalidOperationException("the desktop has no default output");
        // pactl accepts numeric ids and @...@ aliases as well as names. These
        // controls bind exact node names; never let an ambiguous name resolve
        // to a different device or a shared-jack pseudo-output.
        if (sink.Length is 0 or > 256 || sink.Any(char.IsControl) || sink.Contains('#')
            || sink[0] is '@' or '-' || sink.All(char.IsAsciiDigit))
            throw new InvalidOperationException("select an unambiguous PipeWire output name");
        AudioNode? node = _pw.ListDevices().FirstOrDefault(d => d.Name == sink && d.Kind == AudioNodeKind.Sink);
        if (node is null) throw new InvalidOperationException("the selected output is unavailable");
        if (node.IsOwn && !_config.Mixes.Any(m => m.Kind == MixKind.Monitor && m.SinkName == sink))
            throw new InvalidOperationException("internal OpenXLR sinks keep unity gain; select a monitor mix or an external output");
        return sink;
    }
}
