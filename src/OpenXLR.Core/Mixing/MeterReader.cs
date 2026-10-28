using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Live stereo level meters. One capture process per metered sink reads its
/// monitor as interleaved float samples; RMS is accumulated per channel between
/// polls and mapped to a dB scale, which tracks perceived loudness far better
/// than raw sample peaks (a single transient no longer pins the bar).
///
/// Display mapping: 0 = -60 dBFS and below, 1 = 0 dBFS. A short release keeps
/// bars falling smoothly instead of flickering.
/// </summary>
public sealed class MeterReader : IDisposable
{
    private const int SampleRate = 8000;
    private const double FloorDb = -60.0;
    private const double Release = 0.75;

    private readonly Dictionary<string, Meter> _meters = [];
    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private bool _disposed;

    public MeterReader() : this(() => Environment.TickCount64) { }

    internal MeterReader(Func<long> clock) => _clock = clock;

    private sealed class Meter
    {
        public required ProcessStartInfo Start { get; init; }
        public Process? Process;
        public bool Ended = true;
        public int Failures;
        public long StartedAt, RetryAt;
        public double SumL, SumR;    // sums of squares since the last poll
        public long Count;
        public double DispL, DispR;  // smoothed display values
        public int EmptyPolls;       // polls since data last arrived
    }

    /// <summary>Begin metering a sink by monitoring its output.</summary>
    public void Add(string id, string sinkName)
    {
        var psi = new ProcessStartInfo("parec")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add($"{sinkName}.monitor");
        psi.ArgumentList.Add("--format=float32le");
        psi.ArgumentList.Add($"--rate={SampleRate}");
        psi.ArgumentList.Add("--channels=2");
        // Without an explicit latency parec batches its output in ~1 s
        // chunks, which turns a steady signal into a once-a-second spike
        // followed by decay. 50 ms delivers fragments faster than the
        // 15 Hz poll, so bars track the signal.
        psi.ArgumentList.Add("--latency-msec=50");
        // A meter reads one sink and must stay on it. The session manager
        // treats a stream that it linked to the default output as following
        // the default (target.node -1 in the default metadata) and moves it
        // when the default changes, but it never marks a stream in a link
        // group. Its own group, so the meter never shares a driver with
        // anything else. node.dont-reconnect would also stop the marking, but
        // WirePlumber 0.4 destroys such a stream when its target is not ready
        // yet, and a meter starts right after its sink is created.
        psi.ArgumentList.Add($"--property=node.link-group=openxlr-meter-{sinkName}");
        Add(id, psi);
    }

    /// <summary>Begin metering with a capture process of the caller's choosing.</summary>
    internal void Add(string id, ProcessStartInfo psi)
    {
        lock (_gate)
        {
            if (_disposed || _meters.ContainsKey(id)) return;
            var meter = new Meter { Start = psi };
            _meters[id] = meter;
            Start(id, meter);
        }
    }

    // The gate owns every process and both streams, including replacements.
    private void Start(string id, Meter meter)
    {
        Stop(meter);
        meter.StartedAt = _clock();
        try
        {
            Process p = Process.Start(meter.Start) ?? throw new InvalidOperationException("Could not start the meter.");
            meter.Process = p;
            meter.Ended = false;
            // Take both streams here, while the gate still holds the process:
            // Remove and Dispose can retire the meter before either thread is
            // scheduled, and Process.StandardOutput on a disposed process
            // throws, on a raw thread with no handler above it. Reading a
            // stream that was closed underneath fails inside the loops, where
            // it is caught.
            Stream output = p.StandardOutput.BaseStream;
            Stream errors = p.StandardError.BaseStream;
            new Thread(() => Pump(meter, p, output)) { IsBackground = true, Name = $"meter-{id}" }.Start();
            // stderr must be drained: parec blocks once the pipe fills with
            // warnings, which silently freezes its audio output mid-stream.
            new Thread(() => Drain(errors)) { IsBackground = true, Name = $"meter-err-{id}" }.Start();
        }
        catch (Exception)
        {
            Stop(meter);
            Failed(meter);
        }
    }

    private void Failed(Meter meter)
    {
        long now = _clock();
        if (now - meter.StartedAt >= 5000) meter.Failures = 0;
        meter.Failures = Math.Min(meter.Failures + 1, 6);
        meter.RetryAt = now + Math.Min(1000L << (meter.Failures - 1), 30_000);
        meter.Ended = true;
    }

    private static void Stop(Meter meter)
    {
        if (meter.Process is not { } process) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
        process.Dispose();
        meter.Process = null;
    }

    /// <summary>Stop metering one sink; its process goes with it.</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            if (!_meters.Remove(id, out Meter? meter)) return;
            Stop(meter);
        }
    }

    /// <summary>
    /// Sum the squares of every complete stereo float frame in
    /// <paramref name="buf"/>[0..<paramref name="length"/>), move any trailing
    /// partial frame to the front of the buffer, and return its length so
    /// the next read appends after it.
    /// </summary>
    public static (double SumL, double SumR, int Frames, int Carry) AccumulateFrames(byte[] buf, int length)
    {
        double sumL = 0, sumR = 0;
        int frames = 0;
        int i = 0;
        for (; i + 8 <= length; i += 8)
        {
            double l = BitConverter.ToSingle(buf, i);
            double r = BitConverter.ToSingle(buf, i + 4);
            // One non-finite sample from upstream must not poison the smoothing state
            // or the JSON meter message. Square finite samples as doubles so
            // even a finite float outside the normal audio range stays finite.
            if (double.IsFinite(l)) sumL += l * l;
            if (double.IsFinite(r)) sumR += r * r;
            frames++;
        }
        int rest = length - i;
        if (rest > 0) Buffer.BlockCopy(buf, i, buf, 0, rest);
        return (sumL, sumR, frames, rest);
    }

    private static void Drain(Stream errors)
    {
        var buf = new byte[4096];
        try { while (errors.Read(buf, 0, buf.Length) > 0) { } }
        catch (Exception) { /* process ended */ }
    }

    private void Pump(Meter meter, Process process, Stream stdout)
    {
        // A pipe read can end anywhere inside an 8-byte stereo frame; the
        // remainder is carried into the next read so the stream never goes
        // out of alignment (which would turn the meters into noise).
        var buf = new byte[8192];
        int carry = 0;
        try
        {
            while (!_disposed)
            {
                int read;
                try { read = stdout.Read(buf, carry, buf.Length - carry); }
                catch (Exception) { return; }
                if (read <= 0) return;

                (double sumL, double sumR, int frames, carry) = AccumulateFrames(buf, carry + read);

                lock (_gate)
                {
                    meter.SumL += sumL;
                    meter.SumR += sumR;
                    meter.Count += frames;
                }
            }
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(meter.Process, process)) Failed(meter);
        }
    }

    /// <summary>
    /// Smoothed stereo levels per meter as [left, right] in 0..1 (dB scale).
    /// Call at a steady rate; each call consumes the window since the last one.
    /// </summary>
    public IReadOnlyDictionary<string, double[]> Read()
    {
        var result = new Dictionary<string, double[]>();
        lock (_gate)
        {
            foreach ((string id, Meter m) in _meters)
            {
                if (m.Ended && _clock() >= m.RetryAt) Start(id, m);
                if (m.Count > 0)
                {
                    double newL = ToDisplay(Math.Sqrt(m.SumL / m.Count));
                    double newR = ToDisplay(Math.Sqrt(m.SumR / m.Count));
                    m.SumL = m.SumR = 0;
                    m.Count = 0;
                    m.EmptyPolls = 0;
                    // instant attack, smoothed release
                    m.DispL = Math.Max(newL, m.DispL * Release);
                    m.DispR = Math.Max(newR, m.DispR * Release);
                }
                else if (++m.EmptyPolls > 5)
                {
                    // Audio delivery is batched (~300 ms), so a few empty polls
                    // between batches are normal: hold the bar. Only release
                    // once the gap is clearly silence.
                    m.DispL *= Release;
                    m.DispR *= Release;
                }
                if (m.DispL < 0.004) m.DispL = 0;
                if (m.DispR < 0.004) m.DispR = 0;
                result[id] = [Math.Round(m.DispL, 3), Math.Round(m.DispR, 3)];
            }
        }
        return result;
    }

    private static double ToDisplay(double rms)
    {
        if (rms <= 0) return 0;
        double db = 20.0 * Math.Log10(rms);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Meter m in _meters.Values) Stop(m);
            _meters.Clear();
        }
    }
}
