using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class SidechainRoutingTests
{
    [Fact]
    public void CompatibleIndependentSourcesAreAccepted()
    {
        Assert.Null(SidechainRouting.Validate("mix:stream", "channel:chat", 2, 2));
        Assert.Null(SidechainRouting.Validate("mix:stream", "channel:xlr1", 1, 2));
        Assert.Null(SidechainRouting.Validate("xlr1", "channel:xlr2", 1, 1));
    }

    [Fact]
    public void FeedbackAndLossyLayoutsAreRejected()
    {
        Assert.Contains("own output",
            SidechainRouting.Validate("mix:stream", "mix:stream", 2, 2));
        Assert.Contains("feeds that mix",
            SidechainRouting.Validate("xlr1", "mix:stream", 2, 2));
        Assert.Contains("stereo-to-mono",
            SidechainRouting.Validate("mix:stream", "channel:music", 2, 1));
    }
}
