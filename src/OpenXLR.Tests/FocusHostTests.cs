using OpenXLR.UI;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class FocusHostTests
{
    [DesktopBusFact]
    public async Task DesktopServicePassesOnlyRequestedHostAudioProcessesBackToTheDaemon()
    {
        await using var session = await DesktopKeysTests.PrivateBus.Start();
        string directory = Directory.CreateTempSubdirectory("ox-focus-desktop-").FullName;
        string[] names = ["PATH", "FLATPAK_ID", "OPENXLR_FOCUS_HOST"];
        string?[] before = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "flatpak-spawn"), "echo 73");
            Environment.SetEnvironmentVariable("PATH", directory + ":" + before[0]);
            Environment.SetEnvironmentVariable("FLATPAK_ID", "io.github.emaspa.openxlr");
            Environment.SetEnvironmentVariable("OPENXLR_FOCUS_HOST", "1");
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            using var keys = new DesktopKeys(client);
            await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true }, save: false);
            string[] matches = await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["42", "73"]));
            Assert.Equal(["73"], matches);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["42"])));
            Environment.SetEnvironmentVariable("OPENXLR_FOCUS_HOST", null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["42", "73"])));
        }
        finally
        {
            for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], before[i]);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("42,,73")]
    [InlineData("42;touch /tmp/no")]
    [InlineData(" 42")]
    public void CandidateProtocolRefusesInvalidIds(string text)
        => Assert.Throws<InvalidOperationException>(() => FocusProcesses.Parse(text));

    [Fact]
    public void HostRuntimeFollowsTheRunningFlatpakDeployment()
    {
        Assert.Equal("/installation with spaces/commit/files", FocusHost.ApplicationFiles("[Application]\napp-path=/ignored\n[Instance]\napp-path=/installation with spaces/commit/files\n"));
        Assert.Throws<InvalidOperationException>(() => FocusHost.ApplicationFiles("[Instance]\napp-path=relative"));
        Assert.Throws<InvalidOperationException>(() => FocusHost.ApplicationFiles("[Instance]\napp-path=/one\napp-path=/two"));
    }

    [Fact]
    public void FlatpakInstanceKeysCannotAliasNativeProcessIds()
    {
        var native = Assert.Single(FocusProcesses.ParseKeys("2"));
        var sandbox = Assert.Single(FocusProcesses.ParseKeys("2@123"));
        Assert.NotEqual(native, sandbox);
        Assert.Equal("2@123", sandbox.ToString());
        foreach (string invalid in new[] { "2@", "2@0", "2@-1", "2@123@456", "2@bad", "2@01", "2@18446744073709551616" })
            Assert.Throws<InvalidOperationException>(() => FocusProcesses.ParseKeys(invalid));
        Assert.Equal(["2@123"], DesktopFocusQuery.ParseAudioProcesses("('2@123',)", ["2", "2@123"]));
    }

    [Fact]
    public void MatchesOnlyExactOrDescendantCandidatesAndStopsCycles()
    {
        var parents = new Dictionary<int, int?> { [73] = 42, [74] = 73, [75] = 76, [76] = 75 };
        int? Parent(int pid) => parents.GetValueOrDefault(pid);
        Assert.Equal("42", FocusProcesses.Match(42, [42, 73, 74], Parent));
        Assert.Equal("73,74", FocusProcesses.Match(42, [73, 74, 75], Parent));
        Assert.Throws<InvalidOperationException>(() => FocusProcesses.Match(42, [75], Parent));
        Assert.Throws<InvalidOperationException>(() => FocusProcesses.Parse(string.Join(',', Enumerable.Range(1, 1025))));
        Assert.Equal([42, 73], FocusProcesses.Parse("42,73,42"));
        Assert.Equal(["73", "74"], DesktopFocusQuery.ParseAudioProcesses("('73,74',)", ["73", "74", "75"]));
        Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ParseAudioProcesses("('99',)", ["73"]));
        Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ParseAudioProcesses("('73;99',)", ["73"]));
    }

    [Fact]
    public async Task BridgeRequiresOptInAndValidatesRealHelperReplies()
    {
        string directory = Directory.CreateTempSubdirectory("ox-focus-host-").FullName;
        string? oldPath = Environment.GetEnvironmentVariable("PATH"), oldOptIn = Environment.GetEnvironmentVariable("OPENXLR_FOCUS_HOST");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "flatpak-spawn"), """
                dir=${0%/*}
                printf '%s\n' "$@" > "$dir/arguments"
                /bin/cat "$dir/reply"
                """);
            Environment.SetEnvironmentVariable("PATH", directory + ":" + oldPath);
            Environment.SetEnvironmentVariable("OPENXLR_FOCUS_HOST", null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => FocusHost.ReadAsync("42,73", CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, "arguments")));
            Environment.SetEnvironmentVariable("OPENXLR_FOCUS_HOST", "1");
            File.WriteAllText(Path.Combine(directory, "reply"), "73\n");
            Assert.Equal("73", await FocusHost.ReadAsync("42,73", CancellationToken.None));
            string[] args = File.ReadAllLines(Path.Combine(directory, "arguments"));
            Assert.Equal("--host", args[0]);
            Assert.Equal("--watch-bus", args[1]);
            Assert.Contains("42,73", args);
            Assert.Contains("focus-host", string.Join('\n', args));
            File.WriteAllText(Path.Combine(directory, "reply"), "99\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => FocusHost.ReadAsync("42,73", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("OPENXLR_FOCUS_HOST", oldOptIn);
            Directory.Delete(directory, true);
        }
    }
}
