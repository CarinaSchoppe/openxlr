using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class VariableMicEqTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    public void WholeEqReadsAndRetainsEveryBand(int count)
    {
        MicEq eq = new() { Enabled = true, Bands = Enumerable.Range(0, count).Select(_ => new EqBand()).ToArray() };
        Assert.Null(MicEq.Check(eq));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(eq, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        MicEq read = Assert.IsType<MicEq>(MicEq.Read(document.RootElement, out string? error));
        Assert.Null(error);
        Assert.Equal(count, read.Bands.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void BandCountIsBoundedBeforeValuesAreRead(int count)
        => Assert.NotNull(MicEq.Check(new MicEq { Bands = Enumerable.Range(0, count).Select(_ => new EqBand()).ToArray() }));
    [Fact]
    public void StructuralEditsPreserveExistingBandsAndBothBounds()
    {
        MicEq original = MicEq.Default with { Enabled = true };
        MicEq extended = original.WithAddedBand();
        Assert.Equal(original.Bands, extended.Bands.Take(4));
        Assert.Equal(original, extended.WithoutBand(5));
        Assert.Equal(original.Bands.Where((_, index) => index != 1), original.WithoutBand(2).Bands);
        while (extended.Bands.Count < 16) extended = extended.WithAddedBand();
        Assert.Equal(16, extended.Bands.Select(b => b.Frequency).Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => extended.WithAddedBand());
        while (extended.Bands.Count > 1) extended = extended.WithoutBand(extended.Bands.Count);
        Assert.Throws<InvalidOperationException>(() => extended.WithoutBand(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => original.WithoutBand(0));
    }

    [Fact]
    public void SettingsAndProfilesRetainSixteenBandsAndTheirValues()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-variable-eq-").FullName;
        try
        {
            MicEq eq = new() { Enabled = true, Bands = Enumerable.Range(0, 16).Select(i => new EqBand { Gain = i / 4.0 }).ToArray() };
            MixerSettings settings = new() { Eq = new() { ["xlr1"] = eq } };
            string path = Path.Combine(directory, "mixer.json");
            Assert.Null(settings.Save(path));
            Assert.Equal(eq, MixerSettings.Load(path)!.Eq["xlr1"]);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var scene = new OpenXLR.Core.MixerScene { Eq = new() { ["xlr1"] = eq } };
            var read = JsonSerializer.Deserialize<OpenXLR.Core.MixerScene>(JsonSerializer.Serialize(scene, options), options)!;
            SavedMixerValidation.Validate(read);
            Assert.Equal(eq, read.Eq!["xlr1"]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void NativeInvocationKeepsTheFourBandEntryPointAndNamesOtherCounts()
    {
        Assert.Equal(["eq"], NativePluginHost.Arguments(new() { Id = "test", Kind = "eq", Plugin = "4" }, null));
        Assert.Equal(["eq-bands", "16"], NativePluginHost.Arguments(new() { Id = "test", Kind = "eq", Plugin = "16" }, null));
    }
}
