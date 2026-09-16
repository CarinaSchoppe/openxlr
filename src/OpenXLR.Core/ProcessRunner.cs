using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>What a helper process left behind.</summary>
#if OPENXLR_UI
internal sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut, bool Truncated)
#else
public sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut, bool Truncated)
#endif
{
    public string StdoutText => Encoding.UTF8.GetString(Stdout);
    /// <summary>Exit 0 within the time and output limits.</summary>
    public bool Ok => ExitCode == 0 && !TimedOut && !Truncated;
}

/// <summary>
/// The one way OpenXLR runs a helper (pw-dump, pactl, wpctl, systemctl,
/// the diagnostics commands): arguments passed as a list (no shell), the
/// C locale so parsed output never changes with the desktop language, a
/// deadline, a byte cap on each output pipe, and attached descendants
/// killed when either limit is reached, so a runaway helper or a
/// pathological PipeWire graph cannot grow the daemon's heap or park a
/// thread. Compiled into the daemon through OpenXLR.Core and into the
/// window as a linked source file.
/// </summary>
#if OPENXLR_UI
internal static class ProcessRunner
#else
public static class ProcessRunner
#endif
{
    /// <summary>Room for a large PipeWire graph dump many times over.</summary>
    public const int DefaultStdoutCap = 64 * 1024 * 1024;
    /// <summary>Errors are read by people; the head is what matters.</summary>
    public const int DefaultStderrCap = 64 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Run to completion on the calling thread. See <see cref="RunAsync"/>.</summary>
    public static ProcessResult Run(string exe, IReadOnlyList<string> args, TimeSpan? timeout = null,
        int stdoutCap = DefaultStdoutCap, int stderrCap = DefaultStderrCap, bool cLocale = true,
        CancellationToken cancel = default, IReadOnlyDictionary<string, string>? environment = null)
        => RunAsync(exe, args, timeout, stdoutCap, stderrCap, cLocale, cancel, environment).GetAwaiter().GetResult();

    /// <summary>
    /// Run a helper with a deadline and output caps. Throws only when the
    /// process cannot be started; a timeout, a cap breach or a nonzero exit
    /// are reported in the result. Cancellation returns a non-success result.
    /// Descendants still attached to the helper are killed on interruption;
    /// a child already reparented after its parent exited is outside that tree.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan? timeout = null,
        int stdoutCap = DefaultStdoutCap, int stderrCap = DefaultStderrCap, bool cLocale = true,
        CancellationToken cancel = default, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (cancel.IsCancellationRequested) return new ProcessResult(-1, [], "", false, false);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        if (environment is not null)
            foreach ((string name, string value) in environment) psi.Environment[name] = value;
        if (cLocale)
        {
            psi.Environment["LC_ALL"] = "C";
            psi.Environment["LANGUAGE"] = "C";
        }
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? DefaultTimeout);

        // Both pipes drain concurrently so a chatty helper never blocks on a
        // full pipe; a cap breach cancels the other reader and kills the tree.
        using var breach = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        Task<(byte[] Data, bool Truncated, bool Interrupted)> stdout = ReadCappedAsync(p.StandardOutput.BaseStream, stdoutCap, breach);
        Task<(byte[] Data, bool Truncated, bool Interrupted)> stderr = ReadCappedAsync(p.StandardError.BaseStream, stderrCap, breach);

        bool interrupted = false;
        try
        {
            await p.WaitForExitAsync(breach.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            KillTree(p);
        }
        (byte[] outData, bool outTrunc, bool outInterrupted) = await stdout.ConfigureAwait(false);
        (byte[] errData, bool errTrunc, bool errInterrupted) = await stderr.ConfigureAwait(false);
        interrupted |= outInterrupted || errInterrupted;
        bool timedOut = interrupted && limit.IsCancellationRequested && !cancel.IsCancellationRequested;
        if (!p.HasExited) KillTree(p);
        try { p.WaitForExit(); } catch (Exception) { /* reaped */ }
        int exit;
        try { exit = p.ExitCode; } catch (InvalidOperationException) { exit = -1; }
        if (interrupted && cancel.IsCancellationRequested) exit = -1;
        return new ProcessResult(exit, outData, Encoding.UTF8.GetString(errData), timedOut, outTrunc || errTrunc);
    }

    /// <summary>
    /// Run a user-facing program without a deadline or captured output. Its
    /// lifetime belongs to the user: never kill an installer halfway through.
    /// </summary>
    public static async Task<int> RunInteractiveAsync(string exe, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach ((string name, string value) in environment) start.Environment[name] = value;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"failed to start {exe}");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task<(byte[] Data, bool Truncated, bool Interrupted)> ReadCappedAsync(Stream pipe, int cap, CancellationTokenSource breach)
    {
        var buf = new byte[16 * 1024];
        var kept = new MemoryStream();
        try
        {
            int n;
            while ((n = await pipe.ReadAsync(buf, breach.Token).ConfigureAwait(false)) > 0)
            {
                int room = cap - (int)kept.Length;
                if (n > room)
                {
                    if (room > 0) kept.Write(buf, 0, room);
                    breach.Cancel();          // stops the other reader and the wait; the tree is killed there
                    return (kept.ToArray(), true, false);
                }
                kept.Write(buf, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            // A parent can exit before its child's inherited pipes close.
            // Propagate the interrupted read even when the process wait succeeded.
            return (kept.ToArray(), false, true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return (kept.ToArray(), true, false); // output did not reach EOF
        }
        return (kept.ToArray(), false, false);
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
