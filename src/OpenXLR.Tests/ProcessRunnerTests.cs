using System.Diagnostics;
using OpenXLR.Core;

namespace OpenXLR.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task OutputComesBackWholeWithTheExitCodeAndStderr()
    {
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "printf 'out'; printf 'err' >&2; exit 3"]);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("out", r.StdoutText);
        Assert.Equal("err", r.Stderr);
        Assert.False(r.TimedOut);
        Assert.False(r.Truncated);
        Assert.False(r.Ok);
        Assert.True((await ProcessRunner.RunAsync("true", [])).Ok);
    }

    [Fact]
    public async Task ARunawayOutputIsCappedAndTheProcessKilled()
    {
        var sw = Stopwatch.StartNew();
        // Would print for ever; the cap must end it, not the deadline.
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "yes"], TimeSpan.FromSeconds(20), stdoutCap: 256 * 1024);
        Assert.True(r.Truncated);
        Assert.False(r.Incomplete);
        Assert.False(r.TimedOut);
        Assert.Equal(256 * 1024, r.Stdout.Length);
        Assert.InRange(sw.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ADeadlineKillsTheWholeTree()
    {
        var sw = Stopwatch.StartNew();
        // A child of the shell; killing the shell alone would leave it running.
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "sleep 30 & wait"], TimeSpan.FromMilliseconds(400));
        Assert.True(r.TimedOut);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritedOutputMustFinishWithinTheDeadline(bool cancel)
    {
        using var stop = new CancellationTokenSource();
        if (cancel) stop.CancelAfter(TimeSpan.FromMilliseconds(400));
        // The shell exits successfully, but a child retains its output pipes.
        ProcessResult result = await ProcessRunner.RunAsync("sh", ["-c", "sleep 30 & echo $!"],
            cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(400), cancel: stop.Token);
        try
        {
            // The shell's own status survives; the flags say why the run is not Ok.
            Assert.False(result.Ok);
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Incomplete);
            Assert.False(result.Truncated);
            Assert.Equal(!cancel, result.TimedOut);
            Assert.Equal(cancel, result.Cancelled);
        }
        finally
        {
            // The child is already reparented; only clean up the PID our helper returned.
            if (int.TryParse(result.StdoutText.Trim(), out int pid))
                try { using var child = Process.GetProcessById(pid); child.Kill(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task AnAlreadyCancelledRequestDoesNotStartAHelper()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        // A nonexistent executable proves no launch was attempted.
        ProcessResult result = await ProcessRunner.RunAsync("/openxlr-test-missing-helper", [], cancel: stop.Token);
        Assert.False(result.Ok);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);      // no status to keep: nothing ran
    }

    [Fact]
    public async Task InteractiveProgramsKeepArgumentsAndEnvironmentSeparate()
    {
        string output = Path.Combine(Path.GetTempPath(), "openxlr-interactive-" + Guid.NewGuid());
        string? before = Environment.GetEnvironmentVariable("WINEPREFIX");
        try
        {
            int exit = await ProcessRunner.RunInteractiveAsync("sh",
                ["-c", "printf '%s\\n%s' \"$WINEPREFIX\" \"$1\" > \"$2\"; exit 7", "test", "literal ; $HOME", output],
                new Dictionary<string, string> { ["WINEPREFIX"] = "/a prefix with spaces" });
            Assert.Equal(7, exit);
            Assert.Equal("/a prefix with spaces\nliteral ; $HOME", File.ReadAllText(output));
            Assert.Equal(before, Environment.GetEnvironmentVariable("WINEPREFIX"));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task HelpersRunInTheCLocale()
    {
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "printf '%s' \"$LC_ALL\""]);
        Assert.Equal("C", r.StdoutText);
        ProcessResult raw = await ProcessRunner.RunAsync("sh", ["-c", "printf '%s' \"${LC_ALL:-unset}\""], cLocale: false);
        Assert.Equal(Environment.GetEnvironmentVariable("LC_ALL") ?? "unset", raw.StdoutText);   // untouched without the flag
    }
}
