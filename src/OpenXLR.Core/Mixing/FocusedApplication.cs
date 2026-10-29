namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    public string[] FocusedAudioCandidates()
    {
        lock (_gate)
        {
            var (streams, clients) = _pw.ListApplications();
            return streams.Concat(clients).Where(s => s.ProcessId > 0).Select(s => s.FocusProcessKey).Distinct().Take(1025).ToArray();
        }
    }

    public void RouteFocusedAudioProcesses(string[] pids, string channel)
    {
        lock (_gate)
        {
            if (!HasApplicationChannel(channel)) throw new InvalidOperationException("select an application channel");
            var requested = pids.ToHashSet();
            if (requested.Count is 0 or > 1024 || requested.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("invalid focused audio process list");
            var (streams, clients) = _pw.ListApplications();
            string[] identities = streams.Concat(clients).Where(s => requested.Contains(s.FocusProcessKey))
                .Select(s => s.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (identities.Length != 1) throw new InvalidOperationException("the focused application has no unique audio identity; choose an application explicitly");
            AssignApp(identities[0], channel);
        }
    }

}
