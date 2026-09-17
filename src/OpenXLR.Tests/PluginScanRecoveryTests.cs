using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginScanRecoveryTests
{
    private const string Valid = """{"plugins":[{"id":"recovered","name":"Recovered","audioIns":2,"audioOuts":2}]}""";

    [Theory]
    [InlineData("{bad json")]
    [InlineData(Valid + " extra output")]
    [InlineData("")]
    public void InvalidOutputIsAFailureAndAnExplicitRescanCanRecover(string invalid)
    {
        string root = Directory.CreateTempSubdirectory("scan-recovery-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string bundle = Path.Combine(root, "Broken.vst3");
            File.WriteAllText(bundle, "fixture");
            string cacheDir = Path.Combine(root, "cache");
            var logs = new PluginScanLogStore(Path.Combine(root, "logs"));
            int launches = 0;
            ProcessResult Describe(string _) => new(0, Encoding.UTF8.GetBytes(++launches == 1 ? invalid : Valid), "scanner detail", false, false);
            IReadOnlyList<PluginInfo> Scan(bool retry = false) => HostScan.Run(kind, "unused", [root], Vst3Catalog.Bundles,
                Describe, new ScanCache(cacheDir), logs, retry);

            Assert.Empty(Scan());
            var cache = new ScanCache(cacheDir);
            Assert.Null(cache.Lookup(bundle));
            var failure = cache.Failure(bundle)!;
            Assert.Equal("invalid-description", failure.FailureReason);
            Assert.NotNull(failure.FailedAt);
            var entry = Assert.Single(PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).Entries,
                e => e.Outcome == "invalid-description");
            Assert.Contains("scanner detail", File.ReadAllText(Path.Combine(root, "logs", entry.LogId + ".log")));
            Assert.Empty(Scan());
            Assert.Equal(1, launches);
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.Empty(PluginScanDiagnostics.Failures([report]));
            var skipped = Assert.Single(PluginScanDiagnostics.SkippedFailures([report]).Bundles);
            Assert.Equal(bundle, skipped.Path);
            Assert.Equal(failure.FailedAt, skipped.FailedAt);
            Assert.Equal("invalid-description", skipped.Outcome);
            Assert.Single(Scan(retry: true));
            Assert.Single(Scan());
            Assert.Equal(2, launches);
            Assert.False(new ScanCache(cacheDir).KnownFailure(bundle));
            Assert.Equal(0, PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).SkippedFailedCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACorruptPositiveCacheIsInvalidatedAndTheNextScanRecovers(bool retry)
    {
        string root = Directory.CreateTempSubdirectory("scan-corrupt-").FullName;
        try
        {
            string bundle = Path.Combine(root, "Plugin.vst3");
            File.WriteAllText(bundle, "fixture");
            string cacheDir = Path.Combine(root, "cache");
            var cache = new ScanCache(cacheDir);
            cache.Store(bundle, Encoding.UTF8.GetBytes("{bad json"));
            cache.Save();
            int launches = 0;
            ProcessResult Describe(string _) { launches++; return new(0, Encoding.UTF8.GetBytes(Valid), "", false, false); }
            Assert.Empty(HostScan.Run("corrupt-test", "unused", [root], Vst3Catalog.Bundles, Describe,
                new ScanCache(cacheDir), retryFailures: retry));
            Assert.Equal(0, launches);
            cache = new ScanCache(cacheDir);
            Assert.Null(cache.Lookup(bundle));
            Assert.False(cache.KnownFailure(bundle));
            Assert.Single(HostScan.Run("corrupt-test", "unused", [root], Vst3Catalog.Bundles, Describe, cache));
            Assert.Equal(1, launches);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartErrorsStayRetryableIncludingAfterAnOlderFailure(bool previousFailure)
    {
        string root = Directory.CreateTempSubdirectory("scan-start-error-").FullName;
        try
        {
            string bundle = Path.Combine(root, "Plugin.vst3");
            File.WriteAllText(bundle, "fixture");
            string cacheDir = Path.Combine(root, "cache");
            var cache = new ScanCache(cacheDir);
            if (previousFailure) { cache.StoreFailure(bundle, "timeout"); cache.Save(); }
            HostScan.Run("start-error-test", "unused", [root], Vst3Catalog.Bundles,
                _ => throw new IOException("helper unavailable"), cache, retryFailures: true);
            cache = new ScanCache(cacheDir);
            Assert.False(cache.KnownFailure(bundle));
            Assert.Single(HostScan.Run("start-error-test", "unused", [root], Vst3Catalog.Bundles,
                _ => new(0, Encoding.UTF8.GetBytes(Valid), "", false, false), cache));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SkippedCountIsCompleteWhenTheQuietListIsBoundedAndOldFailuresStayUnknown()
    {
        var capture = new PluginScanDiagnostics.Capture("bounded-skips-test");
        for (int i = 0; i < 150; i++) capture.SkipFailure($"/plugins/{i}.vst3", null, null);
        var report = capture.Complete();
        var skipped = PluginScanDiagnostics.SkippedFailures([report]);
        Assert.Equal(150, skipped.Count);
        Assert.Equal(128, skipped.Bundles.Count);
        Assert.All(skipped.Bundles, b => { Assert.Null(b.FailedAt); Assert.Equal("unknown", b.Outcome); });
        Assert.Empty(PluginScanDiagnostics.Failures([report]));
    }
}
