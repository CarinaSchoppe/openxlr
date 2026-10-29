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
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.UnknownMethod: No such method", "updated OpenXLR UI")]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.ServiceUnknown: no owner", "OpenXLR UI running")]
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

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void SystemProcessesNeverMatchEvenAnExactCandidate(int focused)
    {
        int reads = 0;
        var error = Assert.Throws<InvalidOperationException>(() => FocusProcesses.Match(focused, [1, 42], _ => { ++reads; return 1; }));
        Assert.Contains("no focused application", error.Message);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void InitNeverMatchesAnApplicationsParentChain()
    {
        int reads = 0;
        Assert.Throws<InvalidOperationException>(() => FocusProcesses.Match(1, [42], _ => { ++reads; return 1; }));
        Assert.Equal(0, reads);
    }

    [Fact]
    public Task NoAudioClientsGetsAnActionableErrorWithoutStartingGdbus() => WithHelper("gdbus", "exit 99", () =>
    {
        var error = Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ReadAudioProcesses([]));
        Assert.Equal("the focused application has no identifiable PipeWire client", error.Message);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ATimeoutHasItsOwnMessageAndTheNextQueryCanRun() => WithHelper("gdbus", "/bin/sleep 30", () =>
    {
        var error = Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ReadAudioProcesses(["42"]));
        Assert.Contains("timed out", error.Message);
        Assert.Contains("UI", error.Message);
        // A second request must reach the helper rather than retain the gate.
        string helper = Path.Combine(Environment.GetEnvironmentVariable("PATH")!, "gdbus");
        ExecutableScript.Write(helper, "printf \"('42',)\"");
        Assert.Equal(["42"], DesktopFocusQuery.ReadAudioProcesses(["42"]));
        return Task.CompletedTask;
    });

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationDuringTheParentWalkDoesNotReturnMatchesOrAMissingClientError(bool related)
    {
        using var cancel = new CancellationTokenSource();
        int? Parent(int pid) { cancel.Cancel(); return related ? 42 : null; }
        Assert.ThrowsAny<OperationCanceledException>(() => FocusProcesses.Match(42, [73], Parent, cancel.Token));
    }

    [Fact]
    public Task NiriUsesTheSameSocketValueItValidated() => WithHelper("niri", """
        [ "$NIRI_SOCKET" = /tmp/openxlr-session.sock ] || exit 1
        printf '%s' '{"pid":42,"is_focused":true}'
        """, async () =>
    {
        int reads = 0;
        string? Env(string key) => key == "NIRI_SOCKET" ? ++reads == 1 ? "/tmp/openxlr-session.sock" : "/tmp/another-session.sock" : null;
        Assert.Equal(42u, await CompositorFocus.ReadAsync(FocusCompositor.Niri, CancellationToken.None, Env));
        Assert.Equal(1, reads);
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
