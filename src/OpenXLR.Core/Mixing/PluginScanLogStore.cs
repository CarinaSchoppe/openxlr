using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenXLR.Core.Mixing;

/// <summary>What one failed attempt at describing a bundle was, apart from its output.</summary>
/// <param name="Kind">The plugin format, as the catalogue names it.</param>
/// <param name="Bundle">The bundle the scanner was asked about.</param>
/// <param name="Outcome">The evidence outcome the scan recorded.</param>
/// <param name="StartedAt">When the attempt started.</param>
/// <param name="Duration">How long it took, measured around the attempt itself.</param>
/// <param name="ExitCode">The helper's exit status, or null when it could not be read.</param>
/// <param name="TimedOut">True when OpenXLR's own deadline ended the attempt.</param>
/// <param name="OutputCapped">True when an output limit ended it.</param>
/// <param name="Cached">True when the bytes came from the scan cache rather than a launch.</param>
/// <param name="Scanner">The helper that would have been launched, when one is known.</param>
/// <param name="Bridge">The Windows bridge in use, from what discovery already read.</param>
/// <param name="Detail">What the scan itself noted, such as the error that ended parsing.</param>
public sealed record PluginScanAttempt(string Kind, string Bundle, string Outcome,
    DateTimeOffset StartedAt, TimeSpan Duration, int? ExitCode, bool TimedOut, bool OutputCapped,
    bool Cached = false, string? Scanner = null, string? Bridge = null, string? Detail = null)
{
    public bool WineTrace { get; init; }
    public string? WineDebug { get; init; }
}

/// <summary>Where a saved log ended up, or why there is none.</summary>
public readonly record struct PluginScanLogRef(string? Id, string? Note);

/// <summary>
/// The scanner output of a failed bundle scan, kept on disk so the detail
/// the summary cannot hold is still there when someone collects diagnostics.
///
/// The summary each scan records is one short line per bundle, and it has to
/// stay that size: it is serialized into every diagnostics reply, and a
/// bridged plugin can print megabytes. Clipping it to a head and a tail is
/// what lost the one line that mattered in the field, a Wine stack overflow
/// whose first exception sat in the middle of a long unwind. More output
/// from an attempt that failed is written here, with repeated lines collapsed:
/// at most <see cref="StreamCapBytes"/> of standard error normally,
/// <see cref="TraceStreamCapBytes"/> during a deep Wine trace, and
/// <see cref="StdoutCapBytes"/> of standard output per attempt, at most
/// <see cref="MaxFiles"/> logs and <see cref="MaxTotalBytes"/> in total.
/// Nothing here is ever uploaded, and a successful scan writes nothing.
///
/// One log per bundle: a bundle that fails again replaces its own log, so
/// the newest attempt is the one kept and a plugin that fails every start
/// cannot grow the directory. Failing to write a log never fails a scan;
/// the reason is recorded in the evidence entry instead.
/// </summary>
public sealed partial class PluginScanLogStore(string directory)
{
    /// <summary>
    /// Standard error kept per attempt, and the cap the scan asks the process
    /// runner for, so the log holds the whole of what was read. Wine prints
    /// its unwind one frame per line; a quarter of a megabyte is thousands of
    /// them, and the exception that started it is at the top, which the
    /// runner keeps first.
    /// </summary>
    public const int StreamCapBytes = 256 * 1024;

    // Module loads can fill the ordinary budget before the first exception.
    // Read more during a reproduction, then collapse runs before saving a
    // smaller prefix. Both limits apply even when every line is different.
    public const int TraceCaptureBytes = 8 * 1024 * 1024;
    public const int TraceStreamCapBytes = 1024 * 1024;
    private const int HeaderValueBytes = 2048;

    /// <summary>
    /// Standard output kept per attempt. A description that parsed is not
    /// saved at all, so this is only ever a partial or malformed one, and its
    /// beginning is what shows why it did not parse.
    /// </summary>
    public const int StdoutCapBytes = 64 * 1024;

    /// <summary>Logs kept, oldest deleted first.</summary>
    public const int MaxFiles = 24;

    /// <summary>Bytes kept across all logs, oldest deleted first.</summary>
    public const long MaxTotalBytes = 4L * 1024 * 1024;

    private const int HeaderReadBytes = 8 * 1024;

    public string Directory { get; } = directory;

    /// <summary>Where the daemon keeps them: beside the scan cache, under the user's cache directory.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(cacheHome))
                cacheHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cacheHome, "openxlr", "plugin-scan-logs");
        }
    }

    /// <summary>
    /// The name a bundle's log has, in every run and on every machine: the
    /// format it was scanned as and a digest of its path. The evidence entry
    /// carries the same string, which is how a summary line and a log are
    /// held together without the summary carrying the output.
    /// </summary>
    public static string IdFor(string kind, string bundle)
    {
        string prefix = new([.. kind.Where(char.IsAsciiLetterOrDigit).Take(12)]);
        if (prefix.Length == 0) prefix = "scan";
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bundle)))[..16];
        return prefix.ToLowerInvariant() + "-" + digest;
    }

    /// <summary>True for a name this store could have written, and nothing else.</summary>
    public static bool IsLogName(string name)
        => name.Length is > 5 and <= 96 && name.EndsWith(".log", StringComparison.Ordinal)
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    /// <summary>
    /// Write one failed attempt's output, replacing that bundle's previous
    /// log, and trim the directory back to its bounds. Never throws: a store
    /// that cannot write says so in the reference it returns, and the scan
    /// carries on with the short detail it already had.
    /// </summary>
    public PluginScanLogRef Save(PluginScanAttempt attempt, ReadOnlySpan<byte> stdout, bool stdoutCapped,
        string stderr, bool stderrCapped)
    {
        string id = IdFor(attempt.Kind, attempt.Bundle);
        string path = Path.Combine(Directory, id + ".log");
        try
        {
            OpenXlrPaths.EnsurePrivateDir(Directory);
            string text = Compose(id, attempt, PreviousAttempts(path) + 1, stdout, stdoutCapped, stderr, stderrCapped);
            OpenXlrPaths.WriteAtomic(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or System.Security.SecurityException)
        {
            return new(null, "scanner output was not saved: " + ex.Message);
        }
        Trim();
        return new(id, null);
    }

    /// <summary>
    /// Bring the directory back within its bounds, newest first. Another scan
    /// may be deleting the same files, so every step tolerates a file that is
    /// already gone.
    /// </summary>
    public void Trim()
    {
        List<FileInfo> logs;
        DirectoryInfo directory;
        try { directory = new DirectoryInfo(Directory); }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or PathTooLongException) { return; }
        try
        {
            // A write that was interrupted leaves a temporary file behind. An
            // hour is far longer than any write takes, so anything older than
            // that belongs to a process that is not coming back.
            foreach (FileInfo stale in directory.EnumerateFiles("*.tmp", SearchOption.TopDirectoryOnly)
                         .Where(f => f.LinkTarget is null && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                try { stale.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        try
        {
            logs = [.. directory.EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .Where(f => f.LinkTarget is null && IsLogName(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenBy(f => f.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return; }
        long total = 0;
        for (int i = 0; i < logs.Count; i++)
        {
            long size;
            try { size = logs[i].Length; } catch (Exception ex) when (ex is IOException or FileNotFoundException) { continue; }
            total += size;
            if (i < MaxFiles && total <= MaxTotalBytes) continue;
            try { logs[i].Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>How many attempts the log this one replaces had already counted.</summary>
    private static int PreviousAttempts(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] head = new byte[HeaderReadBytes];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            foreach (string line in Encoding.UTF8.GetString(head, 0, read).Split('\n'))
                if (line.StartsWith("attempt: ", StringComparison.Ordinal)
                    && int.TryParse(line.AsSpan("attempt: ".Length).Trim(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int attempts))
                    return Math.Clamp(attempts, 0, int.MaxValue - 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return 0;
    }

    /// <summary>
    /// The file: a header of plain key and value lines, then each stream in
    /// its own labelled section that says how many bytes there were and how
    /// many are here. Nothing is guessed. A reason the attempt ended is only
    /// written when OpenXLR is the one that ended it; where the helper died
    /// on its own, the exit status is reported and left uninterpreted.
    /// </summary>
    internal static string Compose(string id, PluginScanAttempt attempt, int attemptNumber,
        ReadOnlySpan<byte> stdout, bool stdoutCapped, string stderr, bool stderrCapped)
    {
        int errorLimit = attempt.WineTrace ? TraceStreamCapBytes : StreamCapBytes;
        int captureLimit = attempt.WineTrace ? TraceCaptureBytes : StreamCapBytes;
        var text = new StringBuilder();
        text.Append("# OpenXLR failed plugin scan log\n");
        text.Append("id: ").Append(id).Append('\n');
        text.Append("kind: ").Append(Line(attempt.Kind)).Append('\n');
        text.Append("bundle: ").Append(Line(attempt.Bundle)).Append('\n');
        text.Append("outcome: ").Append(Line(attempt.Outcome)).Append('\n');
        text.Append("attempt: ").Append(attemptNumber.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("startedAt: ").Append(attempt.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("durationMs: ").Append(((long)attempt.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("exitCode: ").Append(attempt.ExitCode is int code
            ? code.ToString(CultureInfo.InvariantCulture) : "unknown").Append('\n');
        text.Append("ended: ").Append(attempt.TimedOut
            ? "OpenXLR's scan deadline passed and OpenXLR killed the process tree, so the exit status above is that kill, not the plugin's"
            : attempt.OutputCapped ? "the scan output limit was reached and OpenXLR killed the process tree"
            : attempt.Cached ? "no process ran: these bytes came from the scan cache"
            : "the helper exited on its own").Append('\n');
        text.Append("cached: ").Append(attempt.Cached ? "true" : "false").Append('\n');
        if (attempt.Scanner is { Length: > 0 } scanner) text.Append("scanner: ").Append(Line(scanner)).Append('\n');
        if (attempt.Bridge is { Length: > 0 } bridge) text.Append("bridge: ").Append(Line(bridge)).Append('\n');
        if (attempt.Detail is { Length: > 0 } detail) text.Append("detail: ").Append(Line(detail)).Append('\n');
        text.Append("wineTrace: ").Append(attempt.WineTrace ? "true" : "false").Append('\n');
        text.Append("wineDebug: ").Append(attempt.WineDebug is null ? "inherited default" : Line(attempt.WineDebug)).Append('\n');
        text.Append("captureLimit: stderr ").Append(captureLimit.ToString(CultureInfo.InvariantCulture)).Append(" bytes\n");
        text.Append("limits: stderr ").Append(errorLimit.ToString(CultureInfo.InvariantCulture))
            .Append(" bytes, stdout ").Append(StdoutCapBytes.ToString(CultureInfo.InvariantCulture))
            .Append(" bytes, ").Append(MaxFiles.ToString(CultureInfo.InvariantCulture))
            .Append(" logs, ").Append(MaxTotalBytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes in total\n");
        text.Append("note: this is the scanner's own output. OpenXLR did not load the plugin, and reading this file does not run anything.\n");

        AppendStream(text, "stderr", stderr, Encoding.UTF8.GetByteCount(stderr), captureLimit, errorLimit, stderrCapped);
        int outputKept = Math.Min(stdout.Length, StdoutCapBytes);
        AppendStream(text, "stdout", Encoding.UTF8.GetString(stdout[..outputKept]), stdout.Length,
            StdoutCapBytes, StdoutCapBytes, stdoutCapped);
        return text.ToString();
    }

    /// <summary>
    /// One header value on one line. A plugin path is whatever the file
    /// system allows, a line break included, and a header field that could
    /// carry one would be a header field that can write another.
    /// </summary>
    private static string Line(string value)
    {
        const string marker = " [truncated]";
        string prefix = Utf8Prefix(value, HeaderValueBytes);
        if (prefix.Length != value.Length) prefix = Utf8Prefix(value, HeaderValueBytes - marker.Length) + marker;
        return prefix.ReplaceLineEndings(" ").Replace('\n', ' ').Replace('\r', ' ');
    }

    private static void AppendStream(StringBuilder text, string stream, string value, int had,
        int captureLimit, int savedLimit, bool cappedWhileReading)
    {
        string captured = Utf8Prefix(value, captureLimit);
        string collapsed = CollapseLines(captured);
        int capturedBytes = Encoding.UTF8.GetByteCount(captured);
        int collapsedBytes = Encoding.UTF8.GetByteCount(collapsed);
        string saved = Utf8Prefix(collapsed, savedLimit);
        int kept = Encoding.UTF8.GetByteCount(saved);
        if (capturedBytes < had)
            text.Append('[').Append(stream).Append(" capture kept the first ").Append(capturedBytes)
                .Append(" of ").Append(had).Append(" bytes]\n");
        if (collapsed != captured)
            text.Append('[').Append(stream).Append(" repeated lines collapsed: ").Append(capturedBytes)
                .Append(" bytes became ").Append(collapsedBytes).Append(" bytes]\n");
        text.Append(Section(stream, collapsed == captured ? had : collapsedBytes, kept, cappedWhileReading));
        text.Append(saved);
        if (saved.Length > 0 && saved[^1] != '\n') text.Append('\n');
    }

    // Limit encoded bytes, not UTF-16 characters. A plugin name or Wine
    // message need not be ASCII, and a split surrogate would grow on disk
    // when the UTF-8 encoder replaces it.
    private static string Utf8Prefix(string value, int bytes)
    {
        int end = 0, used = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > bytes) break;
            used += rune.Utf8SequenceLength;
            end += rune.Utf16SequenceLength;
        }
        return value[..end];
    }

    // Only the envelope changes between repeated Wine messages. Keep the
    // plugin label and the whole payload, including addresses and opcodes,
    // so a second fault cannot disappear as another copy of the first.
    [GeneratedRegex(@"^(?:\[?(?:[0-9]{4}-[0-9]{2}-[0-9]{2}[T ])?[0-9]{2}:[0-9]{2}:[0-9]{2}(?:[.,][0-9]+)?\]?\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"(^|\] )(?:(?:[0-9]+\.[0-9]+):)?(?:[0-9a-fA-F]{4,16}:){1,2}(?=(?:trace|fixme|err|warn):)", RegexOptions.CultureInvariant)]
    private static partial Regex WineThread();

    internal static string CollapseLines(string value)
    {
        var text = new StringBuilder();
        string? previous = null;
        int copies = 0;
        for (int start = 0; start < value.Length;)
        {
            int newline = value.AsSpan(start).IndexOfAny('\r', '\n');
            int end = newline < 0 ? value.Length : start + newline;
            string line = value[start..end];
            int next = end;
            if (next < value.Length && value[next] == '\r') next++;
            if (next < value.Length && value[next] == '\n') next++;
            string key = WineThread().Replace(Timestamp().Replace(line, ""), "$1");
            if (key == previous) copies++;
            else
            {
                Count();
                previous = key;
                copies = 1;
            }
            if (copies <= 3) text.Append(value, start, next - start);
            start = next;
        }
        Count();
        return text.ToString();

        void Count()
        {
            if (copies > 3) text.Append("[OpenXLR: ").Append((copies - 3).ToString(CultureInfo.InvariantCulture))
                .Append(" further identical lines omitted]\n");
        }
    }

    private static string Section(string stream, int had, int kept, bool cappedWhileReading)
    {
        string state = kept < had
            ? $"{kept} of {had} kept, {had - kept} dropped from the end by this log's limit"
            : $"{kept} bytes, whole";
        if (cappedWhileReading)
            state += ", and the scan's own output limit stopped the read before the helper finished";
        return $"--- {stream} ({state}) ---\n";
    }
}
