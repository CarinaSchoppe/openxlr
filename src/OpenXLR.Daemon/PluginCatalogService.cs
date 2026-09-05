using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// Discovers LV2 and VST3 plug-ins exclusively in bounded child processes.
/// Results are published atomically, cached by bundle fingerprint, and failed
/// bundles are quarantined after repeated crashes or timeouts.
/// </summary>
public sealed class PluginCatalogService : IHostedService, IDisposable
{
    internal const int MaxScannerOutputBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan ScannerTimeout = TimeSpan.FromSeconds(12);
    private const int QuarantineThreshold = 3;
    private const int MaxBundles = 4096;
    private const int MaxFingerprintEntries = 100_000;
    private readonly ILogger<PluginCatalogService> _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string _cachePath;
    private readonly string _quarantinePath;
    private ScanCache _cache = new();
    private Dictionary<string, FailureRecord> _failures = new(StringComparer.Ordinal);
    private Task? _activeScan;
    private int _disposed;

    public event Action? Changed;

    public PluginCatalogService(ILogger<PluginCatalogService> log)
    {
        _log = log;
        string cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _cachePath = Path.Combine(cache, "openxlr", "plugin-catalog-v1.json");
        _quarantinePath = Path.Combine(config, "openxlr", "plugin-quarantine-v1.json");
    }

    public IReadOnlyList<PluginInfo> Plugins => PluginRegistry.Plugins;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadState();
        PublishCached();
        _activeScan = RescanAsync(force: false, _shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        Task? scan = _activeScan;
        if (scan is null) return;
        try { await scan.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
    }

    public async Task RescanAsync(bool force, CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugins = new List<PluginInfo>();
            plugins.AddRange(await ScanLv2Async(cancellationToken).ConfigureAwait(false));
            IReadOnlyList<string> bundles = DiscoverVst3Bundles();
            foreach (string bundle in bundles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fingerprint;
                try { fingerprint = Fingerprint(bundle); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    FailureRecord failure = RecordFailure(bundle, $"could not fingerprint bundle: {ex.Message}");
                    plugins.Add(FailedPlugin(bundle,
                        failure.Count >= QuarantineThreshold ? "quarantined" : "failed",
                        failure.LastError));
                    continue;
                }
                if (!force && TryCached(bundle, fingerprint, out IReadOnlyList<PluginInfo>? cached))
                {
                    plugins.AddRange(cached);
                    continue;
                }
                if (IsQuarantined(bundle, out FailureRecord? quarantined))
                {
                    plugins.Add(FailedPlugin(bundle, "quarantined", quarantined!.LastError));
                    continue;
                }
                IReadOnlyList<PluginInfo> scanned = await ScanVst3BundleAsync(bundle, cancellationToken)
                    .ConfigureAwait(false);
                plugins.AddRange(scanned);
                if (scanned.All(plugin => plugin.ScanStatus == "ready"))
                    RecordSuccess(bundle, fingerprint, scanned);
            }

            // An uninstalled bundle must not remain in the on-disk cache.
            // Keeping it there would briefly publish a ghost plug-in on the
            // next daemon start before the asynchronous scan completes.
            lock (_stateGate)
            {
                var present = bundles.ToHashSet(StringComparer.Ordinal);
                foreach (string missing in _cache.Bundles.Keys.Where(key => !present.Contains(key)).ToList())
                    _cache.Bundles.Remove(missing);
                foreach (string missing in _failures.Keys.Where(key => !present.Contains(key)).ToList())
                    _failures.Remove(missing);
            }

            // A class ID is the persistent VST3 identity. Never silently let a
            // later search path replace an earlier plug-in with the same ID.
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var deduplicated = new List<PluginInfo>(plugins.Count);
            foreach (PluginInfo plugin in plugins)
            {
                string identity = $"{plugin.Kind}\0{plugin.Plugin}";
                if (plugin.ScanStatus != "ready" || identities.Add(identity))
                    deduplicated.Add(plugin);
                else
                    deduplicated.Add(plugin with
                    {
                        ScanStatus = "rejected",
                        ScanError = "Duplicate plug-in identity; an earlier search path wins.",
                    });
            }
            PluginRegistry.Replace(deduplicated);
            SaveState();
            Changed?.Invoke();
        }
        finally { _scanGate.Release(); }
    }

    public async Task<string?> RetryAsync(string pluginId, bool clearQuarantine,
        CancellationToken cancellationToken)
    {
        string? bundle;
        lock (_stateGate)
        {
            bundle = PluginRegistry.Plugins.FirstOrDefault(plugin => plugin.Plugin == pluginId)?.ModulePath;
            if (bundle is null || !_failures.ContainsKey(bundle)) return "unknown rejected plug-in";
            if (clearQuarantine) _failures.Remove(bundle);
        }
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Retry exactly once. The former implementation scanned here and
            // then immediately scanned again through RescanAsync, inflating a
            // failure counter twice for one user action.
            IReadOnlyList<PluginInfo> result = await ScanVst3BundleAsync(bundle, cancellationToken)
                .ConfigureAwait(false);
            if (result.All(plugin => plugin.ScanStatus == "ready"))
                RecordSuccess(bundle, Fingerprint(bundle), result);
            PluginRegistry.Replace([
                .. PluginRegistry.Plugins.Where(plugin => plugin.ModulePath != bundle),
                .. result,
            ]);
            SaveState();
            Changed?.Invoke();
            return result.FirstOrDefault(plugin => plugin.ScanStatus != "ready")?.ScanError;
        }
        finally { _scanGate.Release(); }
    }

    private void LoadState()
    {
        try
        {
            _cache = ReadDocument<ScanCache>(_cachePath) ?? new ScanCache();
            _failures = ReadDocument<Dictionary<string, FailureRecord>>(_quarantinePath)
                ?? new Dictionary<string, FailureRecord>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogWarning("plug-in scan cache ignored: {Message}", ex.Message);
            _cache = new ScanCache();
            _failures = new Dictionary<string, FailureRecord>(StringComparer.Ordinal);
        }
    }

    private void PublishCached()
    {
        List<PluginInfo> plugins;
        lock (_stateGate)
            plugins = _cache.Bundles.Values.SelectMany(entry => entry.Plugins).ToList();
        PluginRegistry.Replace(plugins);
    }

    private async Task<IReadOnlyList<PluginInfo>> ScanLv2Async(CancellationToken cancellationToken)
    {
        ProcessStartInfo start = DaemonScanStartInfo("lv2");
        ScanResult result = await RunBoundedAsync(start, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            _log.LogWarning("isolated LV2 scan failed: {Error}", result.Error);
            return [FailedPlugin("lv2", "failed", result.Error)];
        }
        try
        {
            return JsonSerializer.Deserialize<List<PluginInfo>>(result.Output, WebSocketHub.Json) ?? [];
        }
        catch (JsonException ex)
        {
            return [FailedPlugin("lv2", "failed", $"invalid scanner output: {ex.Message}")];
        }
    }

    private async Task<IReadOnlyList<PluginInfo>> ScanVst3BundleAsync(string bundle,
        CancellationToken cancellationToken)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "openxlr-vst3-host");
        if (!File.Exists(executable))
            return [FailedPlugin(bundle, "failed", "The packaged VST3 helper is missing.")];
        var start = new ProcessStartInfo(executable);
        start.ArgumentList.Add("--scan");
        start.ArgumentList.Add(bundle);
        ScanResult result = await RunBoundedAsync(start, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            FailureRecord failure = RecordFailure(bundle, result.Error);
            string status = failure.Count >= QuarantineThreshold ? "quarantined" : "failed";
            return [FailedPlugin(bundle, status, failure.LastError)];
        }
        try
        {
            List<PluginInfo> plugins = JsonSerializer.Deserialize<List<PluginInfo>>(result.Output,
                WebSocketHub.Json) ?? [];
            if (plugins.Count == 0)
                return [FailedPlugin(bundle, "rejected", "No compatible audio-effect class was found.")];
            lock (_stateGate) _failures.Remove(bundle);
            return plugins;
        }
        catch (JsonException ex)
        {
            FailureRecord failure = RecordFailure(bundle, $"invalid scanner output: {ex.Message}");
            return [FailedPlugin(bundle,
                failure.Count >= QuarantineThreshold ? "quarantined" : "failed", failure.LastError)];
        }
    }

    internal static async Task<ScanResult> RunBoundedAsync(ProcessStartInfo start,
        CancellationToken cancellationToken)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = Process.Start(start);
        if (process is null) return new ScanResult("", "could not start scanner");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScannerTimeout);
        Task<string> output = ReadBoundedAsync(process.StandardOutput, MaxScannerOutputBytes, timeout.Token);
        Task<string> error = ReadBoundedAsync(process.StandardError, 16 * 1024, timeout.Token);
        Task exit = process.WaitForExitAsync(timeout.Token);
        try
        {
            // Surface a pipe size violation immediately and terminate the
            // producer instead of leaving it blocked on a full OS pipe until
            // the general scanner timeout expires.
            var pending = new List<Task> { exit, output, error };
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            await exit.ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            string stderr = await error.ConfigureAwait(false);
            return process.ExitCode == 0
                ? new ScanResult(stdout, null)
                : new ScanResult(stdout, CleanError(stderr, $"scanner exited with status {process.ExitCode}"));
        }
        catch (OperationCanceledException)
        {
            await TerminateAndDrainAsync(process, output, error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ScanResult("", $"scanner exceeded {ScannerTimeout.TotalSeconds:0}-second timeout");
        }
        catch (InvalidDataException ex)
        {
            await TerminateAndDrainAsync(process, output, error).ConfigureAwait(false);
            return new ScanResult("", ex.Message);
        }
    }

    private static async Task TerminateAndDrainAsync(Process process, params Task<string>[] readers)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        // Observe both redirected-pipe tasks. Their cancellation or size-limit
        // errors are already represented by the caller's result.
        foreach (Task<string> reader in readers)
            try { _ = await reader.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        while (true)
        {
            int count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            if (result.Length + count > limit) throw new InvalidDataException("scanner output exceeded its size limit");
            result.Append(buffer, 0, count);
        }
    }

    private static string CleanError(string value, string fallback)
    {
        string clean = value.Trim();
        return clean.Length == 0 ? fallback : clean.Length <= 2048 ? clean : clean[..2048];
    }

    private static ProcessStartInfo DaemonScanStartInfo(string format)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot locate daemon executable");
        var start = new ProcessStartInfo(executable);
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("cannot locate daemon assembly"));
        start.ArgumentList.Add($"--plugin-scan={format}");
        return start;
    }

    internal static IReadOnlyList<string> DiscoverVst3Bundles()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();
        string? configured = Environment.GetEnvironmentVariable("VST3_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            roots.AddRange(configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        roots.AddRange([
            Path.Combine(home, ".vst3"),
            "/usr/local/lib64/vst3", "/usr/local/lib/vst3",
            "/usr/lib64/vst3", "/usr/lib/vst3",
        ]);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string configuredRoot in roots.Distinct(StringComparer.Ordinal))
        {
            string root;
            try { root = Path.GetFullPath(configuredRoot); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (!Directory.Exists(root)) continue;
            var pending = new Queue<string>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                string directory = pending.Dequeue();
                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(directory).Take(MaxFingerprintEntries + 1).ToArray(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (string child in children)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(child); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    // Never follow directory symlinks: a hostile or broken
                    // plug-in tree could otherwise create cycles or escape a
                    // configured scan root.
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (child.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                        result.Add(Path.GetFullPath(child));
                    else
                        pending.Enqueue(child);
                    if (result.Count >= MaxBundles) return result.ToArray();
                }
            }
        }
        return result.ToArray();
    }

    internal static string Fingerprint(string bundle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string root = Path.GetFullPath(bundle);
        var pending = new Queue<string>();
        var values = new List<string>();
        pending.Enqueue(root);
        int entries = 0;
        while (pending.Count > 0)
        {
            string directory = pending.Dequeue();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaxFingerprintEntries)
                    throw new InvalidDataException($"bundle exceeds {MaxFingerprintEntries} filesystem entries");
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Enqueue(path);
                    continue;
                }
                var file = new FileInfo(path);
                values.Add($"{Path.GetRelativePath(root, path)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\n");
            }
        }
        foreach (string value in values.Order(StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private bool TryCached(string bundle, string fingerprint, out IReadOnlyList<PluginInfo> plugins)
    {
        lock (_stateGate)
        {
            if (_cache.Bundles.TryGetValue(bundle, out ScanCacheEntry? entry) &&
                entry.Fingerprint == fingerprint)
            {
                plugins = entry.Plugins;
                return true;
            }
        }
        plugins = [];
        return false;
    }

    private bool IsQuarantined(string bundle, out FailureRecord? record)
    {
        lock (_stateGate)
        {
            if (_failures.TryGetValue(bundle, out record) && record.Count >= QuarantineThreshold) return true;
        }
        record = null;
        return false;
    }

    private FailureRecord RecordFailure(string bundle, string error)
    {
        lock (_stateGate)
        {
            int count = _failures.GetValueOrDefault(bundle)?.Count + 1 ?? 1;
            var record = new FailureRecord(count, error, DateTimeOffset.UtcNow);
            _failures[bundle] = record;
            _cache.Bundles.Remove(bundle);
            return record;
        }
    }

    private void RecordSuccess(string bundle, string fingerprint, IReadOnlyList<PluginInfo> plugins)
    {
        lock (_stateGate)
        {
            _failures.Remove(bundle);
            _cache.Bundles[bundle] = new ScanCacheEntry(fingerprint, [.. plugins]);
        }
    }

    private void SaveState()
    {
        lock (_stateGate)
        {
            WriteDocument(_cachePath, _cache);
            WriteDocument(_quarantinePath, _failures);
        }
    }

    private static T? ReadDocument<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var file = new FileInfo(path);
        if (file.Length > MaxScannerOutputBytes) throw new InvalidDataException("plug-in cache is too large");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), WebSocketHub.Json);
    }

    private static void WriteDocument<T>(string path, T value)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, WebSocketHub.Json));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static PluginInfo FailedPlugin(string path, string status, string error)
    {
        string id = "scan-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..20];
        string name = path == "lv2" ? "LV2 catalogue" : Path.GetFileNameWithoutExtension(path);
        return new PluginInfo(path == "lv2" ? "lv2" : "vst3", id, name, "", 0, 0, "", "",
            [], [], [], [], ScanStatus: status, ScanError: error, ModulePath: path);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _shutdown.Dispose();
        _scanGate.Dispose();
    }

    internal sealed record ScanResult(string Output, string? Error);
    private sealed record ScanCacheEntry(string Fingerprint, List<PluginInfo> Plugins);
    private sealed record FailureRecord(int Count, string LastError, DateTimeOffset LastFailure);
    private sealed class ScanCache
    {
        public int SchemaVersion { get; init; } = 1;
        public Dictionary<string, ScanCacheEntry> Bundles { get; init; } = new(StringComparer.Ordinal);
    }
}
