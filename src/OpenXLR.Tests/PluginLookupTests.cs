using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginLookupTests
{
    private static PluginInfo Plugin(string kind, string id, string name = "Test")
        => new(kind, id, name, "", 1, 1, "in", "out", [], [], ["in"], ["out"]);

    private sealed class Catalogue(PluginInfo[] plugins) : IReadOnlyList<PluginInfo>
    {
        public int Visits;
        public int Count => plugins.Length;
        public PluginInfo this[int index] { get { Visits++; return plugins[index]; } }
        public IEnumerator<PluginInfo> GetEnumerator()
        {
            foreach (PluginInfo plugin in plugins) { Visits++; yield return plugin; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void RepeatedHitsAndMissesDoNotWalkALargeCatalogueAgain()
    {
        PluginInfo[] plugins = [.. Enumerable.Range(0, 5000).Select(i => Plugin("lv2", $"urn:test:{i}"))];
        var catalogue = new Catalogue(plugins);
        Assert.Same(plugins[^1], PluginCatalog.Find(catalogue, "lv2", plugins[^1].Plugin));
        int visited = catalogue.Visits;
        for (int lookup = 0; lookup < 1000; lookup++)
        {
            Assert.Same(plugins[^1], PluginCatalog.Find(catalogue, "lv2", plugins[^1].Plugin));
            Assert.Null(PluginCatalog.Find(catalogue, "lv2", "missing"));
        }
        Assert.Equal(visited, catalogue.Visits);
    }

    [Fact]
    public void WarmLookupsAllocateNoObjects()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                IReadOnlyList<PluginInfo> plugins = [Plugin("lv2", "urn:test")];
                for (int i = 0; i < 100; i++) PluginCatalog.Find(plugins, "lv2", "urn:test");
                _ = GC.GetAllocatedBytesForCurrentThread();
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) PluginCatalog.Find(plugins, "lv2", "urn:test");
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
                Assert.Equal(0, allocated);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Plugin lookup allocation probe hung.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void MatchingKeepsTheFirstCopyAndExactFormatAndIdentifier()
    {
        PluginInfo first = Plugin("lv2", "urn:test", "first"), second = Plugin("lv2", "urn:test", "second");
        PluginInfo clap = Plugin("clap", "urn:test"), upper = Plugin("lv2", "URN:TEST");
        IReadOnlyList<PluginInfo> plugins = [first, second, clap, upper];
        Assert.Same(first, PluginCatalog.Find(plugins, "lv2", "urn:test"));
        Assert.Same(clap, PluginCatalog.Find(plugins, "clap", "urn:test"));
        Assert.Same(upper, PluginCatalog.Find(plugins, "lv2", "URN:TEST"));
        Assert.Null(PluginCatalog.Find(plugins, "LV2", "urn:test"));
    }

    [Fact]
    public void AReplacementCatalogueUsesItsOwnMetadataAndDoesNotKeepTheOldListAlive()
    {
        PluginInfo next = Plugin("lv2", "urn:test", "replacement");
        WeakReference old = IndexOldCatalogue();
        IReadOnlyList<PluginInfo> replacement = [next];
        Assert.Same(next, PluginCatalog.Find(replacement, "lv2", "urn:test"));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(old.IsAlive, "The lookup cache kept a retired catalogue alive.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference IndexOldCatalogue()
    {
        IReadOnlyList<PluginInfo> plugins = new List<PluginInfo> { Plugin("lv2", "urn:test", "old") };
        Assert.Equal("old", PluginCatalog.Find(plugins, "lv2", "urn:test")!.Name);
        return new WeakReference(plugins);
    }
}
