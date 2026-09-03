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
        if (!Directory.Exists("/usr/lib/lv2/lsp-plugins.lv2")) Skip = "LSP LV2 bundle is not installed in /usr/lib/lv2.";
    }
}
