using System.Text;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class FocusProcessResolutionTests
{
    [Fact]
    public void InvalidInstanceMetadataDoesNotDiscardNativeCandidates()
    {
        Assert.Equal([new FocusProcesses.Candidate(42, null), new(7, "123")],
            FocusProcesses.ParseKeys("42,2@invalid,3@0,4@01,7@123,42"));
    }

    [Fact]
    public void AnEmptyNiriFocusIsReportedAsNoFocusedApplication()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CompositorFocus.Parse(FocusCompositor.Niri, Encoding.UTF8.GetBytes("null")));
        Assert.Contains("no focused application", error.Message);
    }

    [Theory]
    [InlineData("exit 1")]
    [InlineData("/bin/sleep 10")]
    [InlineData(null)]
    public async Task UnavailableFlatpakDiscoveryDoesNotBreakNativeRouting(string? helper)
    {
        await WithHelper("flatpak", helper, async () =>
        {
            int pid = Environment.ProcessId;
            string matches = await FocusProcesses.MatchKeysAsync(pid, [new(pid, null), new(7, "123")], CancellationToken.None);
            Assert.Equal(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), matches);
        });
    }

    [Fact]
    public async Task TwoFlatpakInstancesCanShareOnePidNamespace()
    {
        int pid = Environment.ProcessId;
        await WithHelper("flatpak", $"printf '123 {pid}\\n456 {pid}\\n'", async () =>
        {
            string matches = await FocusProcesses.MatchKeysAsync(pid, [new(pid, "123"), new(1, "456")], CancellationToken.None);
            Assert.Equal($"{pid}@123", matches);
        });
    }

    [Fact]
    public async Task SharedNamespaceDoesNotHideAnAmbiguousAudioProcess()
    {
        int pid = Environment.ProcessId;
        await WithHelper("flatpak", $"printf '123 {pid}\\n456 {pid}\\n'", async () =>
        {
            string matches = await FocusProcesses.MatchKeysAsync(pid, [new(pid, "123"), new(pid, "456")], CancellationToken.None);
            Assert.Equal(new[] { $"{pid}@123", $"{pid}@456" }, matches.Split(',').Order());
        });
    }

    [Fact]
    public Task AnUnmappedSandboxPidCannotAliasTheSameNativePid() => WithHelper("flatpak", null, async () =>
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => FocusProcesses.MatchKeysAsync(
            Environment.ProcessId, [new(Environment.ProcessId, "123")], CancellationToken.None));
    });

    [Theory]
    [InlineData("Error: GDBus.Error:org.openxlr.Desktop.Unavailable: The desktop has no focused application.", "The desktop has no focused application.")]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.UnknownMethod: No such method", "updated OpenXLR window")]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.ServiceUnknown: no owner", "OpenXLR window running")]
    public async Task DesktopFailuresGiveActionableMessagesWithoutRawDbusErrors(string stderr, string expected)
    {
        await WithHelper("gdbus", "printf '%s' '" + stderr + "' >&2\nexit 1", () =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ReadAudioProcesses(["42"]));
            Assert.Contains(expected, error.Message);
            Assert.DoesNotContain("GDBus.Error", error.Message);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void MatchingKeepsExactPrecedenceAndBoundsParentWalks()
    {
        var parents = new Dictionary<int, int?> { [73] = 42, [74] = 73, [75] = 76, [76] = 75 };
        int? Parent(int pid) => parents.GetValueOrDefault(pid);
        Assert.Equal([42], FocusProcesses.Match(42, [42, 73, 74], Parent));
        Assert.Equal([73, 74], FocusProcesses.Match(42, [73, 74, 75], Parent));
        Assert.Throws<InvalidOperationException>(() => FocusProcesses.Match(42, [75], Parent));
        Assert.Throws<InvalidOperationException>(() => FocusProcesses.Match(0, [42], Parent));
        Assert.Throws<InvalidOperationException>(() => FocusProcesses.ParseKeys(string.Join(',', Enumerable.Range(1, 1025))));
        Assert.Throws<InvalidOperationException>(() => FocusProcesses.ParseKeys(new string('1', 32768)));
    }

    [Fact]
    public async Task ACancelledQueryNeverRoutesEvenWhenNativeCandidatesAreAvailable()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FocusProcesses.MatchKeysAsync(Environment.ProcessId, [new(Environment.ProcessId, null)], cancel.Token));
    }

    [Fact]
    public Task MissingGdbusIsReportedAndReleasesTheQueryGate() => WithHelper("gdbus", null, () =>
    {
        for (int i = 0; i < 2; i++)
        {
            var error = Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ReadAudioProcesses(["42"]));
            Assert.Contains("needs gdbus", error.Message);
        }
        return Task.CompletedTask;
    });

    private static async Task WithHelper(string name, string? script, Func<Task> check)
    {
        string directory = Directory.CreateTempSubdirectory("ox-focus-resolution-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            if (script is not null) ExecutableScript.Write(Path.Combine(directory, name), script);
            Environment.SetEnvironmentVariable("PATH", directory);
            await check();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }
}
