using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Bounded, pipe-draining process execution; never waits on the UI thread.</summary>
internal static class ServiceCommand
{
    internal static async Task<bool> RunAsync(string executable, string[] arguments, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return false;
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellation.Token);
            try
            {
                await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            return false;
        }
    }
}
