using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

public sealed class LocalePrefixTests
{
    [Theory]
    [InlineData("de-AT", "de-AT")]
    [InlineData("DE-at", "de-AT")]
    [InlineData("de", "de")]
    [InlineData("sr-Latn", null)]
    public void LongPrivateUseLocalesKeepTheMostSpecificShippedPrefixWithBoundedAllocations(string prefix, string? expected)
    {
        string[] shipped = ["en", "de", "de-AT", "sr-Cyrl"];
        string locale = prefix + "-x-" + string.Join('-', Enumerable.Repeat("a", 1024));
        Localizer.Match(prefix, shipped);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string? matched = Localizer.Match(locale, shipped);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, matched);
        Assert.True(allocated < locale.Length * 128L,
            $"Matching a {locale.Length}-character locale allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData("de-AT", "de-AT")]
    [InlineData("de-AT-x-custom", "de-AT")]
    [InlineData("de-ATmore", "de")]
    [InlineData("deu", null)]
    [InlineData("en-x-de-AT", "en")]
    public void OnlyWholePrefixSubtagsMatchRegardlessOfCatalogueOrder(string locale, string? expected)
    {
        Assert.Equal(expected, Localizer.Match(locale, ["de", "en", "de-AT"]));
        Assert.Equal(expected, Localizer.Match(locale, ["de-AT", "en", "de"]));
    }
}
