using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace OpenXLR.UI;

/// <summary>The bounded process list passed across the optional host bridge.</summary>
internal static class FocusProcesses
{
    internal readonly record struct Candidate(int Pid, string? Instance)
    {
        public override string ToString() => Pid.ToString(CultureInfo.InvariantCulture) + (Instance is null ? "" : "@" + Instance);
    }

    internal static Candidate[] ParseKeys(string text)
    {
        if (text.Length > 32767) throw new InvalidOperationException("Too many focus candidates.");
        string[] parts = text.Split(',');
        if (parts.Length > 1024) throw new InvalidOperationException("Too many focus candidates.");
        return parts.Select(part =>
        {
            string[] fields = part.Split('@');
            if (fields.Length is < 1 or > 2 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                throw new InvalidOperationException("Invalid focus candidate process id.");
            string? instance = fields.Length == 2 ? fields[1] : null;
            if (instance is not null && (!ulong.TryParse(instance, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0 || id.ToString(CultureInfo.InvariantCulture) != instance))
                throw new InvalidOperationException("Invalid Flatpak instance id.");
            return new Candidate(pid, instance);
        }).Distinct().ToArray();
    }

    internal static async System.Threading.Tasks.Task<string> MatchKeysAsync(int focused, Candidate[] candidates, System.Threading.CancellationToken cancel)
    {
        var mapped = new Dictionary<Candidate, int>();
        var wanted = candidates.ToHashSet();
        if (candidates.Any(c => c.Instance is not null))
        {
            ProcessResult result = await ProcessRunner.RunAsync("flatpak", ["ps", "--columns=instance,child-pid"], TimeSpan.FromSeconds(1), 65536, 4096, cancel: cancel);
            if (!result.Ok) throw new InvalidOperationException("Flatpak did not identify its live process namespaces.");
            var namespaces = new Dictionary<string, string>();
            foreach (string line in result.StdoutText.Split('\n'))
            {
                string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length == 2 && int.TryParse(columns[1], out int pid) && pid > 0)
                {
                    string? ns = Namespace(pid);
                    if (ns is not null) namespaces[ns] = columns[0];
                }
            }
            int count = 0;
            foreach (string path in Directory.EnumerateDirectories("/proc"))
            {
                cancel.ThrowIfCancellationRequested();
                if (++count > 65536) throw new InvalidOperationException("Too many live processes for a bounded focus query.");
                if (!int.TryParse(Path.GetFileName(path), out int hostPid)) continue;
                string? ns = Namespace(hostPid);
                if (ns is null || !namespaces.TryGetValue(ns, out string? instance)) continue;
                try
                {
                    string? ids = File.ReadLines(path + "/status").FirstOrDefault(line => line.StartsWith("NSpid:", StringComparison.Ordinal));
                    if (ids is null || !int.TryParse(ids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1], out int pid)) continue;
                    var candidate = new Candidate(pid, instance);
                    if (wanted.Contains(candidate)) mapped[candidate] = hostPid;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        foreach (Candidate candidate in candidates.Where(c => c.Instance is null)) mapped[candidate] = candidate.Pid;
        int[] matches = Parse(Match(focused, mapped.Values.Distinct().ToArray(), Parent));
        return string.Join(',', mapped.Where(pair => matches.Contains(pair.Value)).Select(pair => pair.Key));
    }

    private static string? Namespace(int pid)
    {
        try { return new FileInfo($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/ns/pid").LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static int[] Parse(string text)
    {
        if (text.Length > 11263) throw new InvalidOperationException("Too many focus candidates.");
        string[] parts = text.Split(',');
        if (parts.Length > 1024) throw new InvalidOperationException("Too many focus candidates.");
        return parts.Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
            ? pid : throw new InvalidOperationException("Invalid focus candidate process id.")).Distinct().ToArray();
    }

    internal static string Match(int focused, int[] candidates, Func<int, int?> parent)
    {
        if (focused <= 0) throw new InvalidOperationException("The desktop has no focused application.");
        if (candidates.Contains(focused)) return focused.ToString(CultureInfo.InvariantCulture);
        var parents = new Dictionary<int, int?>();
        bool Related(int child)
        {
            var visited = new HashSet<int>();
            for (int depth = 0; depth < 16 && child > 1 && visited.Add(child); depth++)
            {
                if (!parents.TryGetValue(child, out int? next)) parents[child] = next = parent(child);
                if (next == focused) return true;
                child = next ?? 0;
            }
            return false;
        }
        string result = string.Join(',', candidates.Where(Related));
        return result.Length > 0 ? result : throw new InvalidOperationException("The focused application has no identifiable PipeWire client.");
    }

    internal static int? Parent(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            int end = stat.LastIndexOf(')');
            string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return end >= 0 && fields.Length > 1 && int.TryParse(fields[1], out int parent) ? parent : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
