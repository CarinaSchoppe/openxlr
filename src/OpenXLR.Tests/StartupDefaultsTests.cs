using System.Diagnostics;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class StartupDefaultsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("startup-defaults-").FullName;

    [Fact]
    public void AStartupDefaultQueryIsTrimmed()
    {
        string pactl = ExecutableScript.Write(Path.Combine(_root, "pactl"), "printf 'OpenXLR Monitor\\n'");
        Assert.Equal("OpenXLR Monitor", StartupDefaults.Run(pactl, []));
    }

    [Fact]
    public void AStartupDefaultQueryCannotBlockDaemonStartup()
    {
        string pactl = ExecutableScript.Write(Path.Combine(_root, "pactl"), "printf partial; sleep 60");
        var elapsed = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => StartupDefaults.Run(pactl, [], TimeSpan.FromMilliseconds(100)));
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(75), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AFailedStartupDefaultQueryIsRejected()
    {
        string pactl = ExecutableScript.Write(Path.Combine(_root, "pactl"), "printf unavailable >&2; exit 1");
        var error = Assert.Throws<InvalidOperationException>(() => StartupDefaults.Run(pactl, []));
        Assert.Contains("unavailable", error.Message);
    }

    [Fact]
    public void AnOversizedStartupDefaultCannotBeUsedAsADeviceName()
    {
        string pactl = ExecutableScript.Write(Path.Combine(_root, "pactl"), "head -c 65537 /dev/zero");
        var error = Assert.Throws<InvalidOperationException>(() => StartupDefaults.Run(pactl, []));
        Assert.Contains("exceeded", error.Message);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
