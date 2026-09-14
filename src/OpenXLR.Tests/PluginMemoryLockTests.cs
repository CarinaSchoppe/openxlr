using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class PluginMemoryLockTests
{
    [Theory]
    [InlineData("8388608 8388608 bytes", 8388608L)]
    [InlineData("8388608 unlimited bytes", 8388608L)]
    [InlineData("unlimited unlimited bytes", -1L)]
    [InlineData("0 8388608 bytes", 0L)]
    [InlineData("268435456 268435456 bytes", 268435456L)]
    public void ReadsTheSoftLimitTheHostsInherit(string fields, long expected)
        => Assert.Equal(expected, PluginMemoryLock.ParseLimit(
            "Max open files            1024 524288 files\nMax locked memory         " + fields + "\n"));

    [Theory]
    [InlineData("")]
    [InlineData("Max locked memory         invalid unlimited bytes")]
    [InlineData("Max locked memory         -1 unlimited bytes")]
    [InlineData("Max locked memory         8388608 8388608 kbytes")]
    [InlineData("Max locked memory         999999999999999999999 999999999999999999999 bytes")]
    public void UnreadableLimitsStayUnknown(string text)
        => Assert.Null(PluginMemoryLock.ParseLimit(text));

    [Theory]
    [InlineData(0L, true)]
    [InlineData(8388608L, true)]
    [InlineData(268435455L, true)]
    [InlineData(268435456L, false)]
    [InlineData(-1L, false)]
    [InlineData(null, false)]
    public void WarnsOnlyBelowTheBridgeThreshold(long? limit, bool warn)
    {
        Assert.Equal(warn, PluginMemoryLock.Note(limit, true) is not null);
        Assert.Null(PluginMemoryLock.Note(limit, false));
    }

    [Fact]
    public void SetupExposesTheDaemonLimitAndItsRecoveryAdviceOnTheWire()
    {
        var setup = new PluginSetup(true, "~/.lv2", "~/.clap", "~/.vst3", "5.1.1", true, [], [])
        {
            MemoryLockLimitBytes = 8388608,
            MemoryLockNote = PluginMemoryLock.Note(8388608, true),
        };
        JsonNode reply = JsonNode.Parse(JsonSerializer.Serialize(new PluginSetupMessage(setup)))!;
        Assert.Equal(8388608, reply["memoryLockLimitBytes"]!.GetValue<long>());
        string note = reply["memoryLockNote"]!.GetValue<string>();
        Assert.Contains("8 MiB", note);
        Assert.Contains("running daemon", note);
        Assert.Contains("sign out", note);
        Assert.DoesNotContain("Setup", reply.AsObject().Select(p => p.Key));
    }
}
