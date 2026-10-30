using Avalonia.Media;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using CoreColours = OpenXLR.Core.Mixing.LayoutColours;
using UiColours = OpenXLR.UI.LayoutColours;
using TuiColours = OpenXLR.Tui.LayoutColours;

namespace OpenXLR.Tests;

/// <summary>
/// The colour palette offered beside a colour field: a dozen distinct
/// colours the daemon accepts, each with a name in the catalogue, each
/// filling a soundboard pad with lettering that reads on it, and one table
/// for the daemon, the UI and the terminal mixer.
/// </summary>
public sealed class LayoutColourTests
{
    [Fact]
    public void ThePaletteIsADozenDistinctColoursTheDaemonAccepts()
    {
        Assert.InRange(CoreColours.Palette.Count, 10, 12);
        Assert.Equal(CoreColours.Palette.Count, CoreColours.Palette.Select(c => c.Name).Distinct().Count());
        Assert.Equal(CoreColours.Palette.Count, CoreColours.Palette.Select(c => c.Hex.ToUpperInvariant()).Distinct().Count());
        foreach (LayoutColourDefinition colour in CoreColours.Palette)
        {
            Assert.True(CoreColours.IsHex(colour.Hex), colour.Name);
            Assert.True(LayoutAppearance.IsValid(new LayoutAppearance("", colour.Hex)), colour.Name);
            Assert.Same(colour, CoreColours.Find(colour.Hex.ToLowerInvariant()));
        }
        // The UI and the terminal mixer compile the same file.
        Assert.Equal(CoreColours.Palette.Select(c => (c.Name, c.Hex)), UiColours.Palette.Select(c => (c.Name, c.Hex)));
        Assert.Equal(CoreColours.Palette.Select(c => (c.Name, c.Hex)), TuiColours.Palette.Select(c => (c.Name, c.Hex)));
        Assert.Null(CoreColours.Find("#123456"));
    }

    [Theory]
    [InlineData("#FFFFFF", true)]
    [InlineData("#FBC02D", true)]
    [InlineData("#ffeb3b", true)]
    [InlineData("#000000", false)]
    [InlineData("#1A237E", false)]
    [InlineData("#D32F2F", false)]
    [InlineData("#fff", false)]
    [InlineData("yellow", false)]
    [InlineData(null, false)]
    public void ALightColourIsOneDarkLetteringReadsBetterOn(string? hex, bool light)
    {
        Assert.Equal(light, CoreColours.IsLight(hex));
        Assert.Equal(CoreColours.IsHex(hex), CoreColours.Luminance(hex) is not null);
    }

    [Fact]
    public void EveryPaletteColourHasANameAndItsLetteringReads()
    {
        Color onLight = ((ISolidColorBrush)SkinTokens.Find("Ox.Soundboard.ForegroundOnLight")!.Default!).Color;
        Color onDark = ((ISolidColorBrush)SkinTokens.Find("Ox.Soundboard.ForegroundOnDark")!.Default!).Color;
        foreach (LayoutColourDefinition colour in CoreColours.Palette)
        {
            string name = OpenXLR.UI.ColourSwatches.Name(colour.Name);
            Assert.NotEqual(colour.Name, name);
            Assert.False(string.IsNullOrWhiteSpace(name));
            // WCAG AA for normal text, against the lettering the pad picks for this fill.
            Color lettering = CoreColours.IsLight(colour.Hex) ? onLight : onDark;
            double contrast = Contrast(CoreColours.Luminance(colour.Hex)!.Value, Luminance(lettering));
            Assert.True(contrast >= 4.5, $"{colour.Name} {colour.Hex}: lettering contrast {contrast:0.00}.");
        }
        Assert.Equal(CoreColours.Palette.Count, CoreColours.Palette.Select(c => OpenXLR.UI.ColourSwatches.Name(c.Name)).Distinct().Count());
        Assert.NotEqual("ColourNone", Localizer.Text("ColourNone"));
    }

    private static double Luminance(Color colour) => CoreColours.Luminance($"#{colour.R:x2}{colour.G:x2}{colour.B:x2}")!.Value;

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}
