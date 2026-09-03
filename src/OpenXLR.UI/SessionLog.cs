using System;
using System.Collections.Concurrent;

namespace OpenXLR.UI;

/// <summary>
/// Bounded UI-session breadcrumbs for the local diagnostics archive. No disk
/// or network I/O happens on an audio/UI callback; the exporter redacts them.
/// The daemon's persistent systemd journal complements this session history.
/// </summary>
internal static class SessionLog
{
    private static readonly ConcurrentQueue<string> Entries = new();
    internal static void Write(string area, string message)
    {
        string singleLine = message.ReplaceLineEndings(" | ");
        if (singleLine.Length > 2048) singleLine = singleLine[..2048];
        Entries.Enqueue($"{DateTimeOffset.UtcNow:O} [{area}] {singleLine}");
        while (Entries.Count > 200) Entries.TryDequeue(out _);
    }

    internal static string Snapshot() => string.Join(Environment.NewLine, Entries.ToArray());
}
