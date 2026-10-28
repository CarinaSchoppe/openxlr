using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WindowIconCacheTests
{
    [Fact]
    public void MissingImagesDoNotRetainEveryCompletedLoad()
    {
        IconThemeLookup previous = ApplicationIconImages.Lookup;
        try
        {
            ApplicationIconImages.Lookup = new([], [], null);
            WeakReference[] loads = MissingImages();
            Collect();
            Assert.InRange(loads.Count(n => n.IsAlive), 0, 128);
        }
        finally { ApplicationIconImages.Lookup = previous; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] MissingImages()
    {
        var loads = new WeakReference[3000];
        for (int i = 0; i < loads.Length; i++)
        {
            string name = $"missing-icon-{i}";
            Task<Bitmap?> load = ApplicationIconImages.Load(name, 32);
            loads[i] = new(load);
            Assert.Null(load.GetAwaiter().GetResult());
        }
        return loads;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupResultsKeepOnlyABoundedWindowOfNames(bool found)
    {
        string root = Directory.CreateTempSubdirectory("openxlr-icon-cache-").FullName;
        try
        {
            var lookup = new IconThemeLookup([], [], null);
            WeakReference[] names = LookupNames(lookup, root, found);
            Collect();
            Assert.InRange(names.Count(n => n.IsAlive), 0, 1024);
            GC.KeepAlive(lookup);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LookupNames(IconThemeLookup lookup, string root, bool found)
    {
        var names = new WeakReference[3000];
        for (int i = 0; i < names.Length; i++)
        {
            string name = found ? Path.Combine(root, $"icon-{i}.png") : $"missing-icon-{i}";
            if (found) File.WriteAllText(name, "lookup does not decode");
            names[i] = new(name);
            Assert.Equal(found ? 1 : 0, lookup.Find(name, 32).Count);
        }
        return names;
    }

    [Fact]
    public async Task EqualRequestsShareOneTaskAndReplacingTheLookupDropsOldAnswers()
    {
        IconThemeLookup previous = ApplicationIconImages.Lookup;
        try
        {
            ApplicationIconImages.Lookup = new([], [], null);
            Task<Bitmap?> first = ApplicationIconImages.Load("not-installed", 32);
            Assert.Same(first, ApplicationIconImages.Load("not-installed", 32));
            Assert.Null(await first);
            ApplicationIconImages.Lookup = new([], [], null);
            Task<Bitmap?> next = ApplicationIconImages.Load("not-installed", 32);
            Assert.NotSame(first, next);
            Assert.Null(await next);
        }
        finally { ApplicationIconImages.Lookup = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOversizedNameIsNotKeptByEitherCache(bool image)
    {
        IconThemeLookup previous = ApplicationIconImages.Lookup;
        var lookup = new IconThemeLookup([], [], null);
        try
        {
            ApplicationIconImages.Lookup = lookup;
            WeakReference name = OversizedName(lookup, image);
            Collect();
            Assert.False(name.IsAlive);
            GC.KeepAlive(lookup);
        }
        finally { ApplicationIconImages.Lookup = previous; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OversizedName(IconThemeLookup lookup, bool image)
    {
        string name = new('x', 1_000_000);
        if (image) Assert.Null(ApplicationIconImages.Load(name, 32).GetAwaiter().GetResult());
        else Assert.Empty(lookup.Find(name, 32));
        return new(name);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
