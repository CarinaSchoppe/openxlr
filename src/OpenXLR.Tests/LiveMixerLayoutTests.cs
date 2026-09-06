using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class LiveMixerLayoutTests
{
    [Theory]
    [InlineData("Podcast", "podcast")]
    [InlineData("123", "channel-123")]
    [InlineData("ignore", "ignore-2")]
    [InlineData("🎙", "channel")]
    [InlineData("Studio \"A\"", "studio-a")]
    public void StableIdsAreSafeAndNeverUseTheIgnoreTarget(string name, string id)
        => Assert.Equal(id, Mixer.NewChannelId(name, []));

    [Fact]
    public void StableIdsAvoidExistingHardwareAndApplicationIds()
        => Assert.Equal("xlr1-3", Mixer.NewChannelId("XLR1", ["xlr1", "xlr1-2"]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void InvalidNamesAreRejectedBeforeGraphWork(string? name)
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "createChannel", Name = name }, mixer, _ => null));
    }

    [Fact]
    public void LongNamesAreRejectedAndOrdinaryNamesAccepted()
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "createChannel", Name = new string('a', 61) }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new Command { Cmd = "createChannel", Name = "Studio A" }, mixer, _ => null));
    }
}
