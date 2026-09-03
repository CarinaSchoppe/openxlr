using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class NativeHostContractTests
{
    [Theory]
    [InlineData("key:'clock.rate' value:'48000'", 48000)]
    [InlineData("key:'clock.rate' value:'48000'\nkey:'clock.force-rate' value:'44100'", 44100)]
    [InlineData("key:'clock.rate' value:'96000'\nkey:'clock.force-rate' value:'0'", 96000)]
    public void UsesActualPipeWireGraphRate(string metadata, int expected)
        => Assert.Equal(expected, PipeWireAdapter.ParseGraphSampleRate(metadata));

    [Theory]
    [InlineData("")]
    [InlineData("key:'clock.rate' value:'99999999'")]
    public void UnknownRateCannotSilentlyProduceWrongPitch(string metadata)
        => Assert.Throws<InvalidOperationException>(() => PipeWireAdapter.ParseGraphSampleRate(metadata));
}
