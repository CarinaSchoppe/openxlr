using System.Buffers.Binary;

namespace OpenXLR.Core.Mixing;

/// <summary>What one look at a unit's capture decided.</summary>
/// <param name="Recover">Reset the unit now.</param>
/// <param name="Reason">What was seen, for the journal.</param>
/// <param name="Note">Said once when recoveries are given up for a while.</param>
public readonly record struct CaptureVerdict(bool Recover, string? Reason, string? Note);

/// <summary>
/// Watches the capture of a unit that recovers by a USB reset
/// (<see cref="Devices.DeviceCapabilities.CaptureRecovery"/>) while a mix
/// plays into its own sink, which is when the Wave:3 MK.2's capture stalled.
/// Two signs are taken, both chosen so that a quiet room or a muted
/// microphone never resets the unit:
/// <list type="bullet">
/// <item>The kernel keeps the capture stopped while PipeWire runs the source,
/// for <see cref="StopWindow"/>.</item>
/// <item>The capture repeats itself sample for sample. The buzz the MK.2 gave
/// had its energy only at multiples of 187.5 Hz, 48000 over 256, the block
/// size; no microphone signal repeats a block bit for bit, since the
/// capsule's own noise differs in every block (<see cref="CaptureRepeat"/>).
/// A short sample is taken every <see cref="SampleSpacing"/>, and
/// <see cref="RepeatsToRecover"/> in a row are needed.</item>
/// </list>
/// Recoveries are spaced by <see cref="RecoverySpacing"/>, and after
/// <see cref="MaxRecoveries"/> within <see cref="RecoveryWindow"/> the watch
/// stops resetting the unit and says so once.
/// </summary>
public sealed class CaptureWatch
{
    internal static readonly TimeSpan StopWindow = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan SampleSpacing = TimeSpan.FromSeconds(10);
    internal const int RepeatsToRecover = 3;
    internal static readonly TimeSpan RecoverySpacing = TimeSpan.FromMinutes(2);
    internal const int MaxRecoveries = 3;
    internal static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(30);
    /// <summary>A quarter second at 48 kHz: eight times the longest block looked for.</summary>
    internal const int SampleFrames = 12000;

    private readonly Func<long> _clock;
    private long? _stoppedSince;
    private long? _nextSample;
    private int _repeats;
    private readonly Queue<long> _recoveries = new();
    private bool _gaveUpSaid;

    /// <param name="clock">Milliseconds; tests move it on.</param>
    public CaptureWatch(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>
    /// One sweep's look. <paramref name="playback"/> is null while the unit is
    /// not at risk (no recovery capability, no mix on its own sink), which
    /// starts every count over.
    /// </summary>
    public CaptureVerdict Observe(UnitPlayback? playback, Func<string, CaptureRun> probe, Func<string, float[]?> sample)
    {
        long now = _clock();
        if (playback is not { } unit)
        {
            Forget();
            return default;
        }
        CaptureRun run = probe(unit.Source);
        string? reason = null;
        if (run.NodeRunning && run.KernelRunning == false)
        {
            _stoppedSince ??= now;
            if (now - _stoppedSince >= (long)StopWindow.TotalMilliseconds)
                reason = $"the kernel has kept the capture of {unit.Source} stopped for {StopWindow.TotalSeconds:0} seconds while PipeWire runs it and {unit.Sink} plays";
        }
        else
        {
            _stoppedSince = null;
            // The first sample comes a spacing after the risk began, so a
            // capture that is starting up is never sampled.
            _nextSample ??= now + (long)SampleSpacing.TotalMilliseconds;
            if (run.KernelRunning == true && now >= _nextSample)
            {
                _nextSample = now + (long)SampleSpacing.TotalMilliseconds;
                CaptureRepeat.Result repeat = CaptureRepeat.Measure(sample(unit.Source));
                _repeats = repeat.Repeating ? _repeats + 1 : 0;
                if (_repeats >= RepeatsToRecover)
                    reason = $"the capture of {unit.Source} has repeated the same {repeat.Lag} samples in {RepeatsToRecover} samples taken " +
                             $"{SampleSpacing.TotalSeconds:0} seconds apart (difference {repeat.ResidualDb:0} dB below a {repeat.LevelDb:0} dBFS signal) while {unit.Sink} plays";
            }
        }
        if (reason is null) return default;
        while (_recoveries.Count > 0 && now - _recoveries.Peek() >= (long)RecoveryWindow.TotalMilliseconds) _recoveries.Dequeue();
        if (_recoveries.Count >= MaxRecoveries)
        {
            if (_gaveUpSaid) return default;
            _gaveUpSaid = true;
            return new CaptureVerdict(false, reason,
                $"{reason}; OpenXLR has reset the unit {MaxRecoveries} times in {RecoveryWindow.TotalMinutes:0} minutes and stops for now. " +
                "Unplug it and plug it back in, or move the monitor output off its headphones");
        }
        if (_recoveries.Count > 0 && now - _recoveries.Last() < (long)RecoverySpacing.TotalMilliseconds) return default;
        _recoveries.Enqueue(now);
        _gaveUpSaid = false;
        Forget();
        return new CaptureVerdict(true, reason, null);
    }

    /// <summary>A recovery ran, asked for or not: every count starts over.</summary>
    public void Recovered() => Forget();

    private void Forget()
    {
        _stoppedSince = null;
        _nextSample = null;
        _repeats = 0;
    }

    /// <summary>
    /// A quarter second of a capture source, as interleaved stereo float
    /// samples, read by pw-record at 48 kHz without conversion; null when it
    /// could not be read whole. Runs a helper for about as long as the
    /// sample. The tap is passive, so a source nothing else runs gives no
    /// sample rather than being started by it.
    /// </summary>
    public static float[]? Sample(string source) => Sample(source, "pw-record");

    internal static float[]? Sample(string source, string executable, int frames = SampleFrames)
    {
        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(executable, SampleArguments(source, frames), TimeSpan.FromSeconds(3),
                stdoutCap: frames * 2 * sizeof(float) * 2, stderrCap: 16 * 1024);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        // pw-record 1.6 exits with 1 when it stops at the sample count, so the
        // sample is judged by its length, not the exit code.
        int bytes = frames * 2 * sizeof(float);
        if (result.TimedOut || result.Truncated || result.Stdout.Length < bytes) return null;
        return Floats(result.Stdout.AsSpan(0, bytes));
    }

    internal static IReadOnlyList<string> SampleArguments(string source, int frames = SampleFrames) =>
    [
        "--target", source,
        "--rate", "48000",
        "--channels", "2",
        "--channel-map", "FL,FR",
        "--format", "f32",
        "--raw",
        // Frames, not samples: 12000 give 96000 bytes of stereo float.
        "--sample-count", frames.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // A tap: it must not hold the source open or follow the default.
        "-P", "{ node.passive = true node.dont-reconnect = true media.name = \"OpenXLR capture check\" }",
        "-",
    ];

    internal static float[] Floats(ReadOnlySpan<byte> bytes)
    {
        var samples = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        return samples;
    }
}

/// <summary>
/// Whether a capture repeats one block of samples over and over: the left
/// channel against itself one block later, for blocks of 64 to 2048 frames.
/// A signal from a microphone carries the capsule's own noise, which differs
/// from block to block, so the difference stays near the noise level. A
/// stalled stream that replays a buffer leaves a difference of nothing.
/// </summary>
public static class CaptureRepeat
{
    /// <summary>
    /// The difference, as energy against the signal's, below which the
    /// signal repeats: 90 dB down. A replayed buffer differs by nothing at
    /// all. A microphone would need its own noise 90 dB below what it
    /// records, beyond what a capsule at any useful gain reaches, and a
    /// tone held on a multiple of 187.5 Hz still leaves the noise behind.
    /// </summary>
    internal const double RepeatResidual = 1e-9;
    /// <summary>A mean square below this (-90 dBFS) is silence, a muted unit's zeros among it, and never counts.</summary>
    internal const double QuietestLevel = 1e-9;
    internal static readonly int[] Lags = [64, 128, 256, 512, 1024, 2048];

    /// <summary>What a sample showed: whether it repeats, at which block, how closely and how loud.</summary>
    public readonly record struct Result(bool Repeating, int Lag, double ResidualDb, double LevelDb);

    /// <summary>Measure interleaved stereo samples; too short a sample or none never repeats.</summary>
    public static Result Measure(float[]? interleaved)
    {
        if (interleaved is null) return default;
        int frames = interleaved.Length / 2;
        if (frames < 4 * Lags[^1]) return default;
        double total = 0;
        for (int i = 0; i < frames; i++) total += (double)interleaved[2 * i] * interleaved[2 * i];
        double level = total / frames;
        double levelDb = 10 * Math.Log10(Math.Max(level, 1e-30));
        if (level < QuietestLevel) return new Result(false, 0, 0, levelDb);
        double best = double.MaxValue;
        int bestLag = 0;
        foreach (int lag in Lags)
        {
            double difference = 0, energy = 0;
            for (int i = lag; i < frames; i++)
            {
                double x = interleaved[2 * i], earlier = interleaved[2 * (i - lag)];
                difference += (x - earlier) * (x - earlier);
                energy += x * x;
            }
            if (energy <= 0) continue;
            double ratio = difference / energy;
            if (ratio < best)
            {
                best = ratio;
                bestLag = lag;
            }
        }
        if (bestLag == 0) return new Result(false, 0, 0, levelDb);
        return new Result(best < RepeatResidual, bestLag, 10 * Math.Log10(Math.Max(best, 1e-30)), levelDb);
    }
}
