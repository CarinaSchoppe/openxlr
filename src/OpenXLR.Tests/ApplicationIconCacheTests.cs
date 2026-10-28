using System.Runtime.CompilerServices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class ApplicationIconCacheTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangingMetadataKeepsAtMostOneBoundedWindowOfHints(bool published)
    {
        var resolver = new ApplicationIconResolver(() => []);
        WeakReference[] hints = Populate(resolver, published);
        Collect();
        Assert.InRange(hints.Count(h => h.IsAlive), 0, 1024);
        Assert.Equal(published ? new IconMatch("test-icon", IconSource.IconName) : null,
            resolver.Resolve(new(published ? "test-icon" : null, null, null, "Track 0")));
    }

    [Fact]
    public void OversizedHintsCanResolveWithoutBeingRetained()
    {
        var resolver = new ApplicationIconResolver(() => []);
        WeakReference hints = ResolveOversized(resolver);
        Collect();
        Assert.False(hints.IsAlive, "A large, otherwise unused metadata value stayed in the icon cache.");
        GC.KeepAlive(resolver);
    }

    [Fact]
    public void EqualHintsStillShareTheirCachedResult()
    {
        var resolver = new ApplicationIconResolver(() => []);
        var first = new ApplicationIconHints("test-icon", null, null, "Track");
        IconMatch? result = resolver.Resolve(first);
        Assert.Same(result, resolver.Resolve(first with { }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Populate(ApplicationIconResolver resolver, bool published)
    {
        var references = new WeakReference[10000];
        for (int i = 0; i < references.Length; i++)
        {
            var hints = new ApplicationIconHints(published ? "test-icon" : null, null, null, $"Track {i}");
            references[i] = new WeakReference(hints);
            resolver.Resolve(hints);
        }
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ResolveOversized(ApplicationIconResolver resolver)
    {
        var hints = new ApplicationIconHints("test-icon", null, null, new string('x', 1_000_000));
        Assert.Equal(new IconMatch("test-icon", IconSource.IconName), resolver.Resolve(hints));
        return new WeakReference(hints);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
