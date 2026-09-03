using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PipeWireRoutingTests
{
    [Fact]
    public void StreamTargetRequiresMatchingPublishedSink()
    {
        const string inputs = "42\t101\t-\tPipeWire\tfloat32le 2ch 48000Hz\n";
        const string sinks = "100\tOpenXLR_ch_game\tPipeWire\n101\tOpenXLR_ch_music\tPipeWire\n";

        Assert.True(PipeWireAdapter.IsStreamOnSink(inputs, sinks, 42, "OpenXLR_ch_music"));
        Assert.False(PipeWireAdapter.IsStreamOnSink(inputs, sinks, 42, "OpenXLR_ch_game"));
    }

    [Fact]
    public void UnboundStreamIsNotTreatedAsRouted()
    {
        const string inputs = "42\t4294967295\t-\tPipeWire\tfloat32le 2ch 48000Hz\n";
        const string sinks = "101\tOpenXLR_ch_music\tPipeWire\n";

        Assert.False(PipeWireAdapter.IsStreamOnSink(inputs, sinks, 42, "OpenXLR_ch_music"));
    }

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
