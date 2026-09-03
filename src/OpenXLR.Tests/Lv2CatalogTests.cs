using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class Lv2CatalogTests
{
    [Fact]
    public void ReadsLspUnitsAndNamedChoicesWhenBundleIsInstalled()
    {
        // Optional integration coverage: release builders do not have to ship
        // LSP, but the development machine and users who install it exercise
        // the blank-node unit metadata path in lilv.
        if (!Directory.Exists("/usr/lib/lv2/lsp-plugins.lv2")) return;

        PluginInfo equalizer = Assert.Single(Lv2Catalog.Plugins,
            p => p.Plugin == "http://lsp-plug.in/plugins/lv2/para_equalizer_x8_mono");
        Assert.Equal("hz", equalizer.Params.Single(p => p.Name == "Frequency 0").Unit);
        Assert.Equal("gain", equalizer.Params.Single(p => p.Name == "Gain 0").Unit);
        Assert.NotEmpty(equalizer.Params.Single(p => p.Name == "Filter type 0").ScalePoints);
    }
}
