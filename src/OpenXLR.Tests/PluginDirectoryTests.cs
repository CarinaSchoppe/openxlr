using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginDirectoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("plugin-directory-").FullName;

    private static IEnumerable<string> Bundles(string root, string kind)
        => kind == "vst3" ? Vst3Catalog.Bundles(root) : ClapCatalog.Bundles(root);

    [Theory]
    [InlineData("vst3")]
    [InlineData("clap")]
    public void ACycleDoesNotMultiplyScansOrHideOtherPlugins(string kind)
    {
        string bundle = Path.Combine(_root, "good." + kind);
        File.WriteAllText(bundle, "plugin");
        Directory.CreateSymbolicLink(Path.Combine(_root, "again"), ".");
        int calls = 0;
        var plugins = HostScan.Run(Guid.NewGuid().ToString("N"), "unused", [_root], (path, _) => Bundles(path, kind),
            _ => { calls++; return new ProcessResult(0, "{\"plugins\":[{\"id\":\"good\"}]}"u8.ToArray(), "", false, false); },
            new ScanCache(Path.Combine(_root, "cache")));
        Assert.Single(plugins);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("vst3")]
    [InlineData("clap")]
    public void LinkedRootsAndNestedDirectoriesRemainDiscoverableOnce(string kind)
    {
        string search = Directory.CreateDirectory(Path.Combine(_root, "search")).FullName;
        string vendor = Directory.CreateDirectory(Path.Combine(_root, "vendor", "nested")).FullName;
        File.WriteAllText(Path.Combine(vendor, "effect." + kind), "plugin");
        Directory.CreateSymbolicLink(Path.Combine(search, "first"), "../vendor");
        Directory.CreateSymbolicLink(Path.Combine(search, "second"), "../vendor");
        string linkedRoot = Path.Combine(_root, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, search);

        string found = Assert.Single(Bundles(linkedRoot, kind));
        Assert.Equal("plugin", File.ReadAllText(found));
        Assert.StartsWith(linkedRoot + Path.DirectorySeparatorChar, found);
    }

    [Fact]
    public void AnUnreadableChildDoesNotHideReadableClapPlugins()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;
        string readable = Directory.CreateDirectory(Path.Combine(_root, "readable")).FullName;
        string denied = Directory.CreateDirectory(Path.Combine(_root, "denied")).FullName;
        string plugin = Path.Combine(readable, "effect.clap");
        File.WriteAllText(plugin, "plugin");
        File.SetUnixFileMode(denied, UnixFileMode.None);
        try
        {
            Assert.Equal(plugin, Assert.Single(ClapCatalog.Bundles(_root)));
            // Through the whole scan, the plugin is listed and the folder
            // that was passed over is named. A skip nobody hears of would
            // send a diagnostics archive saying the folder was fine.
            string kind = Guid.NewGuid().ToString("N");
            var found = HostScan.Run(kind, "unused", [_root], ClapCatalog.Bundles,
                _ => new(0, Encoding.UTF8.GetBytes("{\"plugins\":[{\"id\":\"fx\",\"name\":\"Fx\",\"audioIns\":2,\"audioOuts\":2}]}"), "", false, false),
                new ScanCache(Path.Combine(_root, "cache")));
            Assert.Single(found);
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            var error = Assert.Single(report.Entries, e => e.Outcome == "directory-error");
            Assert.Equal(denied, error.Path);
            Assert.Contains(report.Entries, e => e.Outcome == "ok" && e.Path == plugin);
            Assert.Single(PluginScanDiagnostics.Failures([report]));
        }
        finally { File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Theory]
    [InlineData("clap")]
    [InlineData("vst3")]
    public void AMissingSearchRootReturnsNoBundles(string kind)
        => Assert.Empty(Bundles(Path.Combine(_root, "missing"), kind));

    [Fact]
    public void AVst3BundleIsATerminalEvenWhenItContainsOtherBundles()
    {
        string bundle = Directory.CreateDirectory(Path.Combine(_root, "outer.vst3")).FullName;
        File.WriteAllText(Path.Combine(bundle, "inner.vst3"), "not another scan");
        Assert.Equal(bundle, Assert.Single(Vst3Catalog.Bundles(_root)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
