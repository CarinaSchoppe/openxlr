using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>Resolve process identity through live audio clients, without guessing from window titles.</summary>
internal static class FocusedApplication
{
    internal static string Resolve(int pid, IEnumerable<AudioStream> streams, Func<int, int?> parent)
    {
        if (pid <= 0) throw new InvalidOperationException("the desktop has no focused application");
        var candidates = streams.Where(s => s.ProcessId > 0).ToArray();
        var exact = candidates.Where(s => s.ProcessId == pid).Select(s => s.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) throw new InvalidOperationException("the focused process has several audio identities; choose an application explicitly");
        var parents = new Dictionary<int, int?>();
        bool IsChild(int child)
        {
            var visited = new HashSet<int>();
            for (int depth = 0; depth < 16 && child > 1 && visited.Add(child); depth++)
            {
                if (!parents.TryGetValue(child, out int? next)) parents[child] = next = parent(child);
                if (next == pid) return true;
                child = next ?? 0;
            }
            return false;
        }
        var related = candidates.Where(s => IsChild(s.ProcessId)).Select(s => s.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return related.Length == 1 ? related[0] : throw new InvalidOperationException(related.Length == 0
            ? "the focused application has no identifiable PipeWire client"
            : "the focused application has several audio identities; choose an application explicitly");
    }

    internal static int? Parent(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            int end = stat.LastIndexOf(')'); // comm may itself contain spaces and parentheses
            string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return end >= 0 && fields.Length > 1 && int.TryParse(fields[1], out int parent) ? parent : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

public sealed partial class Mixer
{
    public void RouteFocusedApplication(int pid, string channel)
    {
        lock (_gate)
        {
            if (!HasApplicationChannel(channel)) throw new InvalidOperationException("select an application channel");
            string identity = FocusedApplication.Resolve(pid, _pw.ListStreams().Concat(_pw.ListClients()), FocusedApplication.Parent);
            AssignApp(identity, channel);
        }
    }
}
