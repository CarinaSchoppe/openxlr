using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PipeWireRoutingTests
{
    [Theory]
    [InlineData("42\t101\t-\tPipeWire\n", "101\tOpenXLR_ch_music\tPipeWire\n", true)]
    [InlineData("42\t101\t-\tPipeWire\n", "100\tOpenXLR_ch_music\tPipeWire\n", false)]
    [InlineData("42\t101\t-\tPipeWire\n", "101\tOpenXLR_ch_game\tPipeWire\n", false)]
    [InlineData("42\t4294967295\t-\tPipeWire\n", "101\tOpenXLR_ch_music\tPipeWire\n", false)]
    [InlineData("43\t101\t-\tPipeWire\n", "101\tOpenXLR_ch_music\tPipeWire\n", false)]
    [InlineData("", "", false)]
    [InlineData("malformed\n42\n", "101\n", false)]
    public void StreamTargetRequiresMatchingPublishedSink(string inputs, string sinks, bool expected)
        => Assert.Equal(expected, PipeWireAdapter.IsStreamOnSink(inputs, sinks, 42, "OpenXLR_ch_music"));

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
