using System.Diagnostics;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class PluginCatalogServiceTests
{
    [Fact]
    public async Task BoundedScannerCapturesSuccessfulOutput()
    {
        var start = Shell("printf result; printf diagnostic >&2");

        PluginCatalogService.ScanResult result = await PluginCatalogService.RunBoundedAsync(
            start, CancellationToken.None);

        Assert.Equal("result", result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task BoundedScannerReturnsSanitizedProcessFailure()
    {
        var start = Shell("printf ' broken scanner  ' >&2; exit 7");

        PluginCatalogService.ScanResult result = await PluginCatalogService.RunBoundedAsync(
            start, CancellationToken.None);

        Assert.Equal("broken scanner", result.Error);
    }

    [Fact]
    public async Task BoundedScannerRejectsOversizedOutputWithoutWaitingForTimeout()
    {
        var start = new ProcessStartInfo("python3");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"print('x' * {PluginCatalogService.MaxScannerOutputBytes + 1}, end='')");
        var elapsed = Stopwatch.StartNew();

        PluginCatalogService.ScanResult result = await PluginCatalogService.RunBoundedAsync(
            start, CancellationToken.None);

        Assert.Contains("size limit", result.Error);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
    }

    [Fact]
    public async Task CancellingScannerTerminatesItPromptly()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PluginCatalogService.RunBoundedAsync(Shell("sleep 60"), cancel.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), elapsed.Elapsed.ToString());
    }

    [Fact]
    public void FingerprintDoesNotFollowDirectorySymlinks()
    {
        string bundle = Directory.CreateTempSubdirectory("openxlr-vst3-bundle-").FullName;
        string outside = Directory.CreateTempSubdirectory("openxlr-vst3-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(bundle, "module.so"), "stable");
            File.WriteAllText(Path.Combine(outside, "untrusted"), "first");
            Directory.CreateSymbolicLink(Path.Combine(bundle, "escape"), outside);
            string first = PluginCatalogService.Fingerprint(bundle);
            File.WriteAllText(Path.Combine(outside, "untrusted"), "changed");

            Assert.Equal(first, PluginCatalogService.Fingerprint(bundle));
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        return start;
    }
}
