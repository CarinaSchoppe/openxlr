using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class PluginMemoryLockTests
{
    [Theory]
    [InlineData("8388608 unlimited bytes", 8388608L, -1L)]
    [InlineData("0 268435456 bytes", 0L, 268435456L)]
    [InlineData("unlimited unlimited bytes", -1L, -1L)]
    [InlineData("8388608 invalid bytes", 8388608L, null)]
    [InlineData("invalid 8388608 bytes", null, 8388608L)]
    public void ReadsBothLimitsIndependently(string fields, long? soft, long? hard)
        => Assert.Equal((soft, hard), PluginMemoryLock.ParseLimits("Max locked memory   " + fields));

    [Theory]
    [InlineData(-1L)]
    [InlineData(268435456L)]
    public void EnoughHardLimitRecommendsTheUnitSetting(long hard)
    {
        string note = PluginMemoryLock.Note(8388608, hard, true)!;
        Assert.Contains("LimitMEMLOCK=infinity", note);
        Assert.DoesNotContain("sign out", note);
    }

    [Fact]
    public void UnknownHardLimitDoesNotPrescribeASessionChange()
    {
        string note = PluginMemoryLock.Note(8388608, null, true)!;
        Assert.Contains("could not be read", note);
        Assert.DoesNotContain("sign out", note);
    }

    [Fact]
    public void AllDaemonUnitsRequestTheSameMemoryLockAllowance()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../.."));
        Assert.Contains("LimitMEMLOCK=infinity", File.ReadAllText(Path.Combine(root, "packaging/openxlr-daemon.service")));
        Assert.Contains("LimitMEMLOCK=infinity", File.ReadAllText(Path.Combine(root, "src/OpenXLR.UI/UiSettings.cs")));
        Assert.Contains("LimitMEMLOCK = \"infinity\";", File.ReadAllText(Path.Combine(root, "packaging/nix/module.nix")));
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Theory]
    [InlineData("8388608 8388608 bytes", 8388608L)]
    [InlineData("8388608 unlimited bytes", 8388608L)]
    [InlineData("unlimited unlimited bytes", -1L)]
    [InlineData("0 8388608 bytes", 0L)]
    [InlineData("268435456 268435456 bytes", 268435456L)]
    public void ReadsTheSoftLimitTheHostsInherit(string fields, long expected)
        => Assert.Equal(expected, PluginMemoryLock.ParseLimits(
            "Max open files            1024 524288 files\nMax locked memory         " + fields + "\n").Soft);

    [Theory]
    [InlineData("")]
    [InlineData("Max locked memory         invalid unlimited bytes")]
    [InlineData("Max locked memory         -1 unlimited bytes")]
    [InlineData("Max locked memory         8388608 8388608 kbytes")]
    [InlineData("Max locked memory         999999999999999999999 999999999999999999999 bytes")]
    public void UnreadableLimitsStayUnknown(string text)
        => Assert.Null(PluginMemoryLock.ParseLimits(text).Soft);

    [Theory]
    [InlineData(0L, true)]
    [InlineData(8388608L, true)]
    [InlineData(268435455L, true)]
    [InlineData(268435456L, false)]
    [InlineData(-1L, false)]
    [InlineData(null, false)]
    public void WarnsOnlyBelowTheBridgeThreshold(long? limit, bool warn)
    {
        Assert.Equal(warn, PluginMemoryLock.Note(limit, limit, true) is not null);
        Assert.Null(PluginMemoryLock.Note(limit, limit, false));
    }

    [Fact]
    public void SetupExposesTheDaemonLimitAndItsRecoveryAdviceOnTheWire()
    {
        var setup = new PluginSetup(true, "~/.lv2", "~/.clap", "~/.vst3", "5.1.1", true, [], [])
        {
            MemoryLockLimitBytes = 8388608,
            MemoryLockHardLimitBytes = 8388608,
            MemoryLockNote = PluginMemoryLock.Note(8388608, 8388608, true),
        };
        JsonNode reply = JsonNode.Parse(JsonSerializer.Serialize(new PluginSetupMessage(setup)))!;
        Assert.Equal(8388608, reply["memoryLockLimitBytes"]!.GetValue<long>());
        Assert.Equal(8388608, reply["memoryLockHardLimitBytes"]!.GetValue<long>());
        string note = reply["memoryLockNote"]!.GetValue<string>();
        Assert.Contains("8 MiB", note);
        Assert.Contains("running daemon", note);
        Assert.Contains("sign out", note);
        Assert.DoesNotContain("Setup", reply.AsObject().Select(p => p.Key));
    }
}
