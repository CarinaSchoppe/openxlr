using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Bounded, pipe-draining process execution; never waits on the UI thread.</summary>
internal static class ServiceCommand
{
    internal sealed record Result(bool Success, string Output, string Error);

    internal static async Task<bool> RunAsync(string executable, string[] arguments, TimeSpan timeout)
        => (await CaptureAsync(executable, arguments, timeout).ConfigureAwait(false)).Success;

    /// <summary>Also used by diagnostics, with the same timeout and child cleanup guarantees.</summary>
    internal static async Task<Result> CaptureAsync(string executable, string[] arguments, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return new(false, "", "Process could not be started.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellation.Token);
            try
            {
                await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                return new(process.ExitCode == 0, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
                return new(false, "", $"{executable} timed out after {timeout.TotalSeconds:g} seconds.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            return new(false, "", $"Could not run {executable}: {ex.Message}");
        }
    }
}
