using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginSourceRecoveryTests
{
    private const string Description = """
        {"plugins":[{"id":"compressor","name":"Compressor","audioIns":2,"audioOuts":2}]}
        """;

    [Fact]
    public void CachedWrapperFollowsOriginalWindowsModuleUpdatesAndRemoval()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("vst3-source-cache-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Compressor.vst3");
            string win = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win")).FullName;
            string linux = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux")).FullName;
            string original = Path.Combine(dir, "original.vst3");
            File.WriteAllText(original, "MZ original plugin");
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-2));
            string wrapper = Path.Combine(linux, "Compressor.so");
            File.WriteAllText(wrapper, "ELF wrapper");
            File.SetLastWriteTimeUtc(wrapper, DateTime.UtcNow);
            File.CreateSymbolicLink(Path.Combine(win, "Compressor.vst3"), Path.GetRelativePath(win, original));
            var cache = new ScanCache(Path.Combine(dir, "cache"), "test-scanner");
            byte[] description = Encoding.UTF8.GetBytes(Description);
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));

            // Same size and a wrapper newer than both versions of the source.
            // Neither the link's stamp nor the newest file detects this update.
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-1));
            Assert.Null(cache.Lookup(bundle));
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));
            File.Delete(original);
            Assert.Null(cache.Lookup(bundle));
            Assert.Contains(original, HostScan.MissingWindowsModuleDetail(bundle));

            File.WriteAllText(original, "MZ restored updated plugin");
            Assert.Null(cache.Lookup(bundle));
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FilePluginLinksAreInvalidatedWhenTheirTargetChanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("plugin-file-link-").FullName;
        try
        {
            string original = Path.Combine(dir, "original");
            string link = Path.Combine(dir, "effect.clap");
            File.WriteAllText(original, "first");
            File.CreateSymbolicLink(link, original);
            var cache = new ScanCache(Path.Combine(dir, "cache"));
            cache.Store(link, Encoding.UTF8.GetBytes(Description));
            Assert.NotNull(cache.Lookup(link));
            File.WriteAllText(original, "replacement with new parameters");
            Assert.Null(cache.Lookup(link));
            File.Delete(original);
            Assert.Null(cache.Lookup(link));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingBundleLinkIsReportedWithoutStartingTheScanner()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("missing-plugin-link-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "Delay.vst3"), Path.Combine(dir, "gone"));
            Assert.Empty(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles,
                _ => throw new InvalidOperationException("must not run"), new ScanCache(Path.Combine(dir, "cache"))));
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            var failure = Assert.Single(PluginScanDiagnostics.Failures([report]));
            Assert.Equal("source-missing", failure.Outcome);
            Assert.Contains("Restore the original plugin", PluginScanDiagnostics.Sentence([failure]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ALinkLoopDoesNotPreventAnotherBundleFromBeingScanned()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("plugin-source-loop-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "a.vst3"), "b.vst3");
            File.CreateSymbolicLink(Path.Combine(dir, "b.vst3"), "a.vst3");
            File.WriteAllText(Path.Combine(dir, "good.vst3"), "fixture");
            int scans = 0;
            var plugins = HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles,
                _ => { scans++; return new(0, Encoding.UTF8.GetBytes(Description), "", false, false); },
                new ScanCache(Path.Combine(dir, "cache")));
            Assert.Single(plugins);
            Assert.Equal(1, scans);
            Assert.Equal(2, PluginScanDiagnostics.Failures(
                PluginScanDiagnostics.Snapshot().Where(r => r.Kind == kind)).Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(false, false, "windows-module-missing")]
    [InlineData(true, false, "timeout")]
    [InlineData(false, true, "output-limit")]
    public void YabridgeMissingSourceFailureKeepsTheExitCodeAndRecoveryAdvice(bool timedOut, bool truncated, string expected)
    {
        string dir = Directory.CreateTempSubdirectory("vst3-source-report-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Compressor.vst3");
            Directory.CreateDirectory(bundle);
            Assert.Empty(HostScan.Run("vst3", "unused", [dir], Vst3Catalog.Bundles,
                _ => new ProcessResult(1, [], "Compressor.vst3 does not contain a Windows VST3 module.", timedOut, truncated),
                new ScanCache(Path.Combine(dir, "cache"))));
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "vst3");
            var entry = Assert.Single(report.Entries, e => e.Path == bundle);
            Assert.Equal(expected, entry.Outcome);
            Assert.Equal(1, entry.ExitCode);
            if (!timedOut && !truncated)
            {
                Assert.Contains("Restore the original Windows plugin", entry.Detail);
                Assert.Contains("original Windows plugin is missing", PluginScanDiagnostics.Sentence(PluginScanDiagnostics.Failures([report])));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
