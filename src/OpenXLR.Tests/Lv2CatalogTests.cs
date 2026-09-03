using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class Lv2CatalogTests
{
    [RequiresLspFact]
    public void ReadsLspUnitsAndNamedChoicesWhenBundleIsInstalled()
    {
        PluginInfo equalizer = Assert.Single(Lv2Catalog.Plugins,
            p => p.Plugin == "http://lsp-plug.in/plugins/lv2/para_equalizer_x8_mono");
        Assert.Equal("hz", equalizer.Params.Single(p => p.Name == "Frequency 0").Unit);
        Assert.Equal("gain", equalizer.Params.Single(p => p.Name == "Gain 0").Unit);
        Assert.NotEmpty(equalizer.Params.Single(p => p.Name == "Filter type 0").ScalePoints);
        Assert.True(equalizer.HasNativeUi);
    }
}

/// <summary>Missing optional integration dependencies are skips, never false passes.</summary>
public sealed class RequiresLspFactAttribute : FactAttribute
{
    public RequiresLspFactAttribute()
    {
        // Fedora installs 64-bit bundles under lib64; Debian/Arch use lib.
        string[] paths = (Environment.GetEnvironmentVariable("LV2_PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries);
        if (!paths.Concat(["/usr/lib/lv2", "/usr/lib64/lv2", "/usr/local/lib/lv2"])
                .Any(p => Directory.Exists(Path.Combine(p, "lsp-plugins.lv2"))))
            Skip = "LSP LV2 bundle is not installed in a standard/configured search directory.";
    }
}
