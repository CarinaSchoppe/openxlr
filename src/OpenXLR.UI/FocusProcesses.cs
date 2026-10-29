using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace OpenXLR.UI;

/// <summary>Match live PipeWire process keys to the focused host process.</summary>
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
        var candidates = new HashSet<Candidate>();
        foreach (string part in parts)
        {
            string[] fields = part.Split('@');
            if (fields.Length is < 1 or > 2 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                continue;
            string? instance = fields.Length == 2 ? fields[1] : null;
            if (instance is not null && !ValidInstance(instance)) continue;
            candidates.Add(new Candidate(pid, instance));
        }
        return candidates.ToArray();
    }

    private static bool ValidInstance(string instance) =>
        ulong.TryParse(instance, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id > 0
        && id.ToString(CultureInfo.InvariantCulture) == instance;

    internal static async System.Threading.Tasks.Task<string> MatchKeysAsync(int focused, Candidate[] candidates, System.Threading.CancellationToken cancel)
    {
        var mapped = new Dictionary<Candidate, int>();
        var wanted = candidates.Where(c => c.Instance is not null).ToHashSet();
        if (wanted.Count > 0)
        {
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync("flatpak", ["ps", "--columns=instance,child-pid"], TimeSpan.FromSeconds(1), 65536, 4096, cancel: cancel);
                cancel.ThrowIfCancellationRequested();
                if (result.Ok) MapInstances(result.StdoutText, wanted, mapped, cancel);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                // Discovery failure makes only the sandbox candidates unavailable.
                mapped.Clear();
            }
        }
        cancel.ThrowIfCancellationRequested();
        foreach (Candidate candidate in candidates.Where(c => c.Instance is null)) mapped[candidate] = candidate.Pid;
        var matches = Match(focused, mapped.Values.Distinct().ToArray(), Parent).ToHashSet();
        return string.Join(',', mapped.Where(pair => matches.Contains(pair.Value)).Select(pair => pair.Key));
    }

    private static void MapInstances(string text, HashSet<Candidate> wanted, Dictionary<Candidate, int> mapped,
        System.Threading.CancellationToken cancel)
    {
        var instances = wanted.Select(c => c.Instance!).ToHashSet(StringComparer.Ordinal);
        var namespaces = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 2 || !instances.Contains(columns[0])
                || !int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0) continue;
            string? ns = Namespace(pid);
            if (ns is null) continue;
            if (!namespaces.TryGetValue(ns, out var group)) namespaces[ns] = group = new(StringComparer.Ordinal);
            group.Add(columns[0]);
        }
        if (namespaces.Count == 0) return;
        int count = 0;
        foreach (string path in Directory.EnumerateDirectories("/proc"))
        {
            cancel.ThrowIfCancellationRequested();
            if (!int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out int hostPid)) continue;
            if (++count > 65536)
            {
                mapped.Clear();
                return;
            }
            string? ns = Namespace(hostPid);
            if (ns is null || !namespaces.TryGetValue(ns, out var group)) continue;
            try
            {
                string? ids = File.ReadLines(path + "/status").FirstOrDefault(line => line.StartsWith("NSpid:", StringComparison.Ordinal));
                if (ids is null || !int.TryParse(ids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1], out int pid)) continue;
                foreach (string instance in group)
                {
                    var candidate = new Candidate(pid, instance);
                    if (wanted.Contains(candidate)) mapped[candidate] = hostPid;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? Namespace(int pid)
    {
        try { return new FileInfo($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/ns/pid").LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static int[] Match(int focused, int[] candidates, Func<int, int?> parent)
    {
        if (focused <= 0) throw new InvalidOperationException("The desktop has no focused application.");
        if (candidates.Contains(focused)) return [focused];
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
        int[] result = candidates.Where(Related).ToArray();
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
