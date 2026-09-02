using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PipeWireRoutingTests
{
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
