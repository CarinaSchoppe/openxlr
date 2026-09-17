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

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
