using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PipeWireRoutingTests
{
    [Fact]
    public void ModulePropertyLabelsEscapeBothParsingLevelsWithoutHtmlEscapes()
    {
        const string name = "OpenXLR \"Game\" / John's \\ audio";
        string encoded = PipeWireAdapter.ModuleProperties(name, "openxlr.internal=true");
        Assert.DoesNotContain("\\u0022", encoded);
        string properties = System.Text.Json.JsonSerializer.Deserialize<string>(encoded)!;
        Assert.StartsWith("node.description=\"OpenXLR \\\"Game\\\" / John's \\\\ audio\" ", properties);
        Assert.EndsWith("openxlr.internal=true", properties);
    }

    [Theory]
    [InlineData("{\"openxlr.internal\":true}", true)]
    [InlineData("{\"openxlr.internal\":\"true\"}", true)]
    [InlineData("{\"openxlr.internal\":false}", false)]
    [InlineData("{\"node.name\":\"OpenXLR_ch_game\"}", false)]
    public void InternalStagesAreNotUserDeviceChoices(string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, PipeWireAdapter.IsInternalDevice(document.RootElement));
    }

    private static readonly string[] Ports = ["FL", "FR", "AUX0", "AUX1"];

    [Fact]
    public void SelectPair_ReturnsRequestedStereoPair()
        => Assert.Equal(["AUX0", "AUX1"], PipeWireAdapter.SelectPair(Ports, 1));

    [Fact]
    public void SelectPair_MissingPairNeverFallsBackToFirstPair()
        => Assert.Empty(PipeWireAdapter.SelectPair(["FL", "FR"], 5));

    [Fact]
    public void SelectPair_KeepsARealFinalMonoPort()
        => Assert.Equal(["AUX0"], PipeWireAdapter.SelectPair(["FL", "FR", "AUX0"], 1));

    [Fact]
    public void SelectPair_RejectsNegativeOffsets()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => PipeWireAdapter.SelectPair(Ports, -1));
}
