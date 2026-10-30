namespace OpenXLR.Core.Mixing;

/// <summary>
/// The driven unit's capture and its own sink while a mix plays into that
/// sink: what the daemon's capture watch looks at.
/// </summary>
public readonly record struct UnitPlayback(string Source, string Sink);

/// <summary>
/// Capture first, and the way back when a unit's capture stalls under its
/// own playback. The hold that keeps a route out of a unit's sink until its
/// capture runs lives in <c>HoldPlaybackLocked</c>; this part probes the
/// capture and takes a unit's playback off while the daemon resets it.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>
    /// How long the routes into a unit's sink wait for its capture after a
    /// USB reset before they are made anyway. The Wave:3 MK.2 came back from a
    /// reset with its capture running in about twelve seconds.
    /// </summary>
    internal static readonly TimeSpan CaptureRecoveryLimit = TimeSpan.FromSeconds(30);

    /// <summary>The capture probe; tests replace it. Null asks the graph and the kernel (<see cref="PipeWireAdapter.CaptureRunOf"/>).</summary>
    internal Func<string, CaptureRun>? CaptureProbe { get; init; }

    private CaptureRun ProbeCaptureLocked(string source) => CaptureProbe is { } probe ? probe(source) : _pw.CaptureRunOf(source);

    /// <summary>The source node a strip is fed from now, or before it is wired the driven unit's.</summary>
    private string? CaptureSourceLocked(string strip)
        => _stripSources.GetValueOrDefault(strip) ?? (_bindings.ContainsKey(strip) ? null : _inputDevice);

    // The unit whose playback is off while it recovers: its card, the id its
    // capture source had when the recovery began, and when it began.
    private sealed record Recovery(string Card, int? Node, long Since);
    private Recovery? _captureRecovery;

    /// <summary>Whether a unit's playback is held off while its capture recovers.</summary>
    public bool CaptureRecoveryActive { get { lock (_gate) return _captureRecovery is not null; } }

    /// <summary>
    /// The driven unit's capture source and the sink of it that a monitor
    /// route plays into, while both are linked; null otherwise.
    /// </summary>
    public UnitPlayback? DrivenUnitPlayback()
    {
        lock (_gate)
        {
            if (!_built || _inputDevice is not { } source) return null;
            if (!_config.Channels.Any(c => c.InputPair is not null && !_bindings.ContainsKey(c.Id) && _inputFeeds.ContainsKey(c.Id)))
                return null;
            string? sink = UnitRoutesLocked(InterfaceNodes.CardKey(source)).Select(r => r.Target).FirstOrDefault();
            return sink is null ? null : new UnitPlayback(source, StripMarker(sink));
        }
    }

    /// <summary>The monitor routes that play into a card's sinks, with pairs linked.</summary>
    private List<(string Key, string Target)> UnitRoutesLocked(string card)
        => [.. MonitorRouteTargetsLocked().Where(r => StripMarker(r.Target) is var bare
                && bare.StartsWith("alsa_output.", StringComparison.Ordinal) && InterfaceNodes.CardKey(bare) == card
                && _monitorRoutes.GetValueOrDefault(r.Key) is { Pairs.Count: > 0 })];

    /// <summary>
    /// Take every mix off the driven unit's own sink and keep it off until
    /// the unit's capture comes back under a new node and runs, which a USB
    /// reset of the unit brings about, or until <see cref="CaptureRecoveryLimit"/>
    /// has passed. The number of routes taken off; null when no unit is
    /// driven.
    /// </summary>
    public int? BeginCaptureRecovery()
    {
        lock (_gate)
        {
            if (!_built || _inputDevice is not { } source) return null;
            string card = InterfaceNodes.CardKey(source);
            List<(string Key, string Target)> routes = UnitRoutesLocked(card);
            foreach ((string key, _) in routes)
            {
                _pw.Unlink(_monitorRoutes[key]);
                _monitorRoutes[key] = new PortLink([]);
            }
            _captureRecovery = new Recovery(card, ProbeCaptureLocked(source).NodeId, Clock());
            ReleasePlaybackHoldLocked(card);
            return routes.Count;
        }
    }

    /// <summary>Stop holding the unit's playback for a recovery, as when its reset failed.</summary>
    public void EndCaptureRecovery()
    {
        lock (_gate) _captureRecovery = null;
    }
}
