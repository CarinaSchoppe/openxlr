namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    /// <summary>
    /// Change the default for applications without an explicit assignment.
    /// Save before invalidating placements; a failed save changes no routing.
    /// </summary>
    public void SetRouteAppsByDefault(bool enabled, Func<MixerSettings, string?>? persist = null)
    {
        lock (_gate)
        {
            bool previous = Matcher.RouteByDefault;
            Matcher.RouteByDefault = enabled;
            try { if (persist is not null) PersistLocked(persist); }
            catch { Matcher.RouteByDefault = previous; throw; }
            if (previous == enabled) return;
            foreach (int id in _streams.Where(p => !Matcher.Overrides.ContainsKey(p.Value.Identity)).Select(p => p.Key).ToArray())
                _streams.Remove(id);
            // Silent apps stay in the list. Their next playback is classified
            // again when automatic routing is restored.
            if (!enabled)
                foreach ((string identity, StreamAssignment app) in _apps.ToArray())
                    if (!Matcher.Overrides.ContainsKey(identity))
                        _apps[identity] = app with { ChannelId = StreamMatcher.Ignore };
        }
    }
}
