using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class ScanCacheSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scan-cache-safety-").FullName;
    private string CacheDirectory => Path.Combine(_root, "cache");
    private string Index => Path.Combine(CacheDirectory, "index.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private string Store(string name)
    {
        string bundle = Path.Combine(_root, name);
        File.WriteAllText(bundle, "plugin");
        var cache = new ScanCache(CacheDirectory, "scanner");
        cache.Store(bundle, "{}"u8.ToArray());
        cache.Save();
        return bundle;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIndexCannotReadOrDeleteFilesOutsideItsCache(bool absolute)
    {
        string bundle = Store("plugin.clap");
        string other = Path.Combine(_root, "unrelated.json");
        File.WriteAllText(other, "keep this file");
        var entries = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry>>(File.ReadAllText(Index), Options)!;
        entries[bundle] = entries[bundle] with { File = absolute ? other : "../unrelated.json" };
        File.WriteAllText(Index, JsonSerializer.Serialize(entries, Options));

        var cache = new ScanCache(CacheDirectory, "scanner");
        byte[]? description = cache.Lookup(bundle);
        File.Delete(bundle);
        cache.Save();
        Assert.True(File.Exists(other), "Cache cleanup must not delete the indexed outside path.");
        Assert.Equal("keep this file", File.ReadAllText(other));
        Assert.Null(description);
    }

    [Fact]
    public void NullAndMismatchedEntriesDoNotHideOtherCachedBundles()
    {
        string valid = Store("valid.clap");
        string mismatched = Store("mismatched.clap");
        var entries = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry?>>(File.ReadAllText(Index), Options)!;
        string broken = Path.Combine(_root, "broken.clap");
        entries[broken] = null;
        entries[mismatched] = entries[mismatched]! with { File = entries[valid]!.File };
        File.WriteAllText(Index, JsonSerializer.Serialize(entries, Options));

        var cache = new ScanCache(CacheDirectory, "scanner");
        Assert.Null(cache.Lookup(broken));
        Assert.Null(cache.Lookup(mismatched));
        Assert.Equal("{}"u8.ToArray(), cache.Lookup(valid));
        File.Delete(mismatched);
        cache.Save();
        Assert.Equal("{}"u8.ToArray(), new ScanCache(CacheDirectory, "scanner").Lookup(valid));
    }

    [Fact]
    public void ValidFailureMarkersSurviveIndexValidationAndCannotDeleteOtherFiles()
    {
        string bundle = Store("plugin.clap");
        var cache = new ScanCache(CacheDirectory, "scanner");
        cache.StoreFailure(bundle, "timeout");
        cache.Save();
        Assert.Equal("timeout", new ScanCache(CacheDirectory, "scanner").Failure(bundle)!.FailureReason);

        string other = Path.Combine(_root, "unrelated.json");
        File.WriteAllText(other, "keep this file");
        var entries = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry>>(File.ReadAllText(Index), Options)!;
        entries[bundle] = entries[bundle] with { File = other };
        File.WriteAllText(Index, JsonSerializer.Serialize(entries, Options));
        cache = new ScanCache(CacheDirectory, "scanner");
        Assert.False(cache.KnownFailure(bundle));
        cache.Invalidate(bundle);
        cache.Save();
        Assert.Equal("keep this file", File.ReadAllText(other));
    }

    [Fact]
    public void AnOversizedDescriptionIsAMissAndCanBeReplaced()
    {
        string bundle = Store("plugin.clap");
        var entries = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry>>(File.ReadAllText(Index), Options)!;
        string path = Path.Combine(CacheDirectory, entries[bundle].File);
        using (var file = File.OpenWrite(path)) file.SetLength((long)ProcessRunner.DefaultStdoutCap + 1);
        var cache = new ScanCache(CacheDirectory, "scanner");
        Assert.Null(cache.Lookup(bundle));
        cache.Store(bundle, "{}"u8.ToArray());
        cache.Save();
        Assert.Equal("{}"u8.ToArray(), new ScanCache(CacheDirectory, "scanner").Lookup(bundle));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ProcessRunner.DefaultStdoutCap)]
    public void DescriptionSizesAtTheBoundsRemainReadable(int length)
    {
        string bundle = Store("plugin.clap");
        var entries = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry>>(File.ReadAllText(Index), Options)!;
        string path = Path.Combine(CacheDirectory, entries[bundle].File);
        using (var file = File.OpenWrite(path)) file.SetLength(length);
        Assert.Equal(length, Assert.IsType<byte[]>(new ScanCache(CacheDirectory, "scanner").Lookup(bundle)).Length);
    }

    [Fact]
    public void ALinkedDescriptionIsNotReadAndCleanupPreservesItsTarget()
    {
        string bundle = Store("plugin.clap");
        var entries = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry>>(File.ReadAllText(Index), Options)!;
        string description = Path.Combine(CacheDirectory, entries[bundle].File);
        string other = Path.Combine(_root, "unrelated.json");
        File.WriteAllText(other, "keep this file");
        File.Delete(description);
        File.CreateSymbolicLink(description, other);
        var cache = new ScanCache(CacheDirectory, "scanner");
        byte[]? read = cache.Lookup(bundle);
        File.Delete(bundle);
        cache.Save();
        Assert.Equal("keep this file", File.ReadAllText(other));
        Assert.Null(read);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CyclicDirectoryLinksDoNotCreateACachedDescription(bool twoDirectories)
    {
        if (!OperatingSystem.IsLinux()) return;
        string bundle = Directory.CreateDirectory(Path.Combine(_root, "Effect.vst3")).FullName;
        File.WriteAllText(Path.Combine(bundle, "module.so"), "fixture");
        if (twoDirectories)
        {
            string other = Directory.CreateDirectory(Path.Combine(_root, "resources")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(bundle, "resources"), other);
            Directory.CreateSymbolicLink(Path.Combine(other, "parent"), bundle);
        }
        else Directory.CreateSymbolicLink(Path.Combine(bundle, "parent"), ".");

        Assert.Null(ScanCache.Stamp(bundle));
        var cache = new ScanCache(CacheDirectory, "scanner");
        cache.Store(bundle, "{}"u8.ToArray());
        Assert.Null(cache.Lookup(bundle));
        cache.StoreFailure(bundle, "timeout");
        Assert.False(cache.KnownFailure(bundle));
    }

    [Fact]
    public void MultipleBackLinksDoNotMultiplyTheBundleWalk()
    {
        if (!OperatingSystem.IsLinux()) return;
        string bundle = Directory.CreateDirectory(Path.Combine(_root, "Effect.vst3")).FullName;
        File.WriteAllText(Path.Combine(bundle, "module.so"), "fixture");
        Directory.CreateSymbolicLink(Path.Combine(bundle, "a"), ".");
        Directory.CreateSymbolicLink(Path.Combine(bundle, "b"), ".");
        Assert.Null(ScanCache.Stamp(bundle));
    }

    [Fact]
    public void ResourceAliasesRemainDistinctAndShareTheEntryBudget()
    {
        if (!OperatingSystem.IsLinux()) return;
        string bundle = Directory.CreateDirectory(Path.Combine(_root, "Effect.vst3")).FullName;
        string resources = Directory.CreateDirectory(Path.Combine(_root, "resources")).FullName;
        File.WriteAllText(Path.Combine(resources, "preset.txt"), "fixture");
        Directory.CreateSymbolicLink(Path.Combine(bundle, "a"), resources);
        Directory.CreateSymbolicLink(Path.Combine(bundle, "b"), resources);

        Assert.Equal(new[] { Path.Combine(bundle, "a", "preset.txt"), Path.Combine(bundle, "b", "preset.txt") },
            ScanCache.BundleFiles(bundle, entryBudget: 4).Order(StringComparer.Ordinal));
        Assert.Throws<IOException>(() => ScanCache.BundleFiles(bundle, entryBudget: 3).ToArray());
        var cache = new ScanCache(CacheDirectory, "scanner");
        cache.Store(bundle, "{}"u8.ToArray());
        Assert.Equal("{}"u8.ToArray(), cache.Lookup(bundle));
        File.WriteAllText(Path.Combine(resources, "preset.txt"), "updated fixture");
        Assert.Null(cache.Lookup(bundle));
    }

    [Fact]
    public void HiddenResourcesAreIncludedInTheBundleIdentity()
    {
        string bundle = Directory.CreateDirectory(Path.Combine(_root, "Effect.vst3")).FullName;
        string resources = Directory.CreateDirectory(Path.Combine(bundle, ".resources")).FullName;
        string file = Path.Combine(resources, ".preset");
        File.WriteAllText(file, "fixture");
        var cache = new ScanCache(CacheDirectory, "scanner");
        cache.Store(bundle, "{}"u8.ToArray());
        Assert.Equal("{}"u8.ToArray(), cache.Lookup(bundle));
        File.WriteAllText(file, "changed fixture");
        Assert.Null(cache.Lookup(bundle));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void AnUnreadableResourceDirectoryDoesNotProduceAPartialFingerprint()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess) return;
        string bundle = Directory.CreateDirectory(Path.Combine(_root, "Effect.vst3")).FullName;
        File.WriteAllText(Path.Combine(bundle, "module.so"), "fixture");
        string resources = Directory.CreateDirectory(Path.Combine(bundle, "resources")).FullName;
        File.WriteAllText(Path.Combine(resources, "preset"), "fixture");
        UnixFileMode mode = File.GetUnixFileMode(resources);
        try
        {
            File.SetUnixFileMode(resources, UnixFileMode.None);
            Assert.Null(ScanCache.Stamp(bundle));
        }
        finally { File.SetUnixFileMode(resources, mode); }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
