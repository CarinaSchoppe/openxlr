using System.Diagnostics;
using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>Owns one bounded, trusted PipeWire sample-delay helper process.</summary>
internal sealed class CompensationDelayHost : IDisposable
{
    public const int MaximumSamples = 2_000_000;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _outputReader;
    private readonly Task _errorReader;
    private long _lastHeartbeat = Stopwatch.GetTimestamp();
    private int _disposed;
    private string _error = "";

    internal CompensationDelayHost(string nodeName, int channels, int delaySamples, int sampleRate)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (delaySamples is < 0 or > MaximumSamples) throw new ArgumentOutOfRangeException(nameof(delaySamples));
        string executable = Path.Combine(AppContext.BaseDirectory, "openxlr-delay-host");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Native latency-compensation helper is missing; rebuild/install the complete OpenXLR package.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            nodeName,
            channels.ToString(CultureInfo.InvariantCulture),
            delaySamples.ToString(CultureInfo.InvariantCulture),
            sampleRate.ToString(CultureInfo.InvariantCulture),
        }) start.ArgumentList.Add(argument);
        Process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start latency-compensation helper.");
        _outputReader = ReadOutputAsync();
        _errorReader = ReadErrorsAsync();
    }

    internal Process Process { get; }
    internal bool IsHealthy => !Process.HasExited
        && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHeartbeat)) < TimeSpan.FromSeconds(5);
    internal string Error => _error;

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await Process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false) is string line)
                if (line is "ready" or "heartbeat")
                    Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await Process.StandardError.ReadLineAsync(_stop.Token).ConfigureAwait(false) is string line)
                _error = line.Length <= 2048 ? line : line[..2048];
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!Process.HasExited)
            {
                Process.StandardInput.WriteLine("quit");
                Process.StandardInput.Flush();
                Process.WaitForExit(1000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        _stop.Cancel();
        try { if (!Process.HasExited) { Process.Kill(entireProcessTree: true); Process.WaitForExit(1000); } }
        catch (InvalidOperationException) { }
        Task.WhenAll(_outputReader, _errorReader).GetAwaiter().GetResult();
        Process.Dispose();
        _stop.Dispose();
    }
}
