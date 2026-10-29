using System.Text;
using OpenXLR.Daemon;
using OpenXLR.UI;
using Tmds.DBus.Protocol;
using PrivateBus = OpenXLR.Tests.DesktopKeysTests.PrivateBus;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class CompositorFocusTests
{
    [Theory]
    [InlineData("KDE", "/stale/sway", "stale_hypr", 0)]
    [InlineData("plasma", null, null, 0)]
    [InlineData("plasmawayland", null, null, 0)]
    [InlineData("plasmax11", null, null, 0)]
    [InlineData("Sway:wlroots", "/sway", "stale_hypr", 1)]
    [InlineData("Hyprland", "/stale", "own_hypr", 2)]
    [InlineData("GNOME", "/stale", "stale_hypr", 3)]
    [InlineData("KDE:Hyprland", null, null, 3)]
    [InlineData(null, "/sway", null, 1)]
    [InlineData("", null, "own_hypr", 2)]
    [InlineData(null, "/sway", "own_hypr", 3)]
    [InlineData(null, null, null, 0)]
    public void DesktopIdentityWinsOverStaleOrAmbiguousIpcHints(string? desktop, string? sway, string? hyprland, int expected)
    {
        string? Env(string key) => key switch { "XDG_CURRENT_DESKTOP" => desktop, "SWAYSOCK" => sway,
            "HYPRLAND_INSTANCE_SIGNATURE" => hyprland, _ => null };
        Assert.Equal((FocusCompositor)expected, CompositorFocus.Select(Env));
        Assert.Equal(FocusCompositor.Unavailable, CompositorFocus.Select(key => key == "XDG_SESSION_DESKTOP" ? "GNOME" : null));
    }

    [Theory]
    [InlineData("{\"pid\":42,\"mapped\":true,\"hidden\":false,\"title\":\"ignore me\"}", true)]
    [InlineData("{\"pid\":2147483647}", true)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    [InlineData("{\"pid\":0}", false)]
    [InlineData("{\"pid\":-2}", false)]
    [InlineData("{\"pid\":4294967295}", false)]
    [InlineData("{\"pid\":2.5}", false)]
    [InlineData("{\"pid\":\"42\"}", false)]
    [InlineData("{\"pid\":42,\"pid\":43}", false)]
    [InlineData("{\"pid\":42,\"mapped\":false}", false)]
    [InlineData("{\"pid\":42,\"hidden\":true}", false)]
    [InlineData("{\"pid\":42,\"hidden\":null}", false)]
    public void HyprlandUsesOnlyAUsableFocusedProcess(string json, bool accepted)
    {
        if (accepted) Assert.True(CompositorFocus.Parse(FocusCompositor.Hyprland, Encoding.UTF8.GetBytes(json)) > 0);
        else Assert.Throws<InvalidOperationException>(() => CompositorFocus.Parse(FocusCompositor.Hyprland, Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwayWalksBothTiledAndFloatingTreesWithoutMatchingTitles(bool floating)
    {
        string list = floating ? "floating_nodes" : "nodes";
        string json = $$"""
            {"type":"root","nodes":[{"type":"workspace","focused":true,
            "{{list}}":[{"pid":999,"focused":false,"name":"Expected title"},
                        {"pid":42,"focused":true,"name":"$(no execution)","nodes":[]}]}]}
            """;
        // A workspace has no process; only the selected leaf supplies one.
        Assert.Equal(42u, CompositorFocus.Parse(FocusCompositor.Sway, Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("{\"nodes\":[],\"floating_nodes\":[]}")]
    [InlineData("{\"focused\":true,\"pid\":0}")]
    [InlineData("{\"focused\":true,\"pid\":\"42\"}")]
    [InlineData("{\"focused\":\"true\",\"pid\":42}")]
    [InlineData("{\"nodes\":{}}")]
    [InlineData("{\"nodes\":[null]}")]
    [InlineData("{\"focused\":true,\"pid\":42,\"nodes\":[{\"focused\":true,\"pid\":43}]}")]
    [InlineData("{\"pid\":42,\"focused\":false,\"focused\":true}")]
    public void SwayRefusesMalformedAbsentAndAmbiguousFocus(string json)
        => Assert.Throws<InvalidOperationException>(() => CompositorFocus.Parse(FocusCompositor.Sway, Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("sway")]
    [InlineData("hyprland")]
    public Task ReadOnlyHelpersReceiveTheExactSessionAndAreQueriedAgain(string name) => WithHelpers(async directory =>
    {
        var source = Source(name);
        SetReply(name, 42);
        Assert.Equal(42u, await CompositorFocus.ReadAsync(source, CancellationToken.None));
        string[] expected = name == "sway" ? ["-s", Path.Combine(directory, "sway.sock"), "-r", "-t", "get_tree"]
            : ["-j", "-i", "test_instance", "activewindow"];
        Assert.Equal(expected, File.ReadAllLines(Path.Combine(directory, "arguments")));
        SetReply(name, 43);
        Assert.Equal(43u, await CompositorFocus.ReadAsync(source, CancellationToken.None));
    });

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("../some_instance")]
    [InlineData("other_instance;echo bad")]
    [InlineData("other_instance\n")]
    public Task AHyprlandSignatureNeverSelectsAnIndexOrInjectsOptions(string instance) => WithHelpers(async directory =>
    {
        Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompositorFocus.ReadAsync(FocusCompositor.Hyprland, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory, "arguments")));
    });

    [Theory]
    [InlineData("timeout")]
    [InlineData("oversized")]
    [InlineData("failure")]
    [InlineData("missing")]
    public Task AFailedOrIncompleteHelperNeverProvidesAFallbackProcess(string failure) => WithHelpers(async directory =>
    {
        string helper = Path.Combine(directory, "swaymsg");
        if (failure == "missing") File.Delete(helper);
        else ExecutableScript.Write(helper, failure switch
        {
            "timeout" => "#!/bin/sh\n/bin/sleep 30\n",
            "oversized" => "#!/bin/sh\n/usr/bin/head -c 2097153 /dev/zero\n",
            _ => "#!/bin/sh\necho '{\"focused\":true,\"pid\":999}'\nexit 1\n",
        });
        if (failure == "missing") Environment.SetEnvironmentVariable("PATH", directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompositorFocus.ReadAsync(FocusCompositor.Sway, CancellationToken.None));
    });

    [Fact]
    public Task ACancelledQueryDoesNotLaunchAHelper() => WithHelpers(async directory =>
    {
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompositorFocus.ReadAsync(FocusCompositor.Sway, stopped.Token));
        Assert.False(File.Exists(Path.Combine(directory, "arguments")));
    });

    [Fact]
    public Task ConcurrentAndCancelledQueriesReleaseTheServiceGate() => WithHelpers(async directory =>
    {
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "sway");
        string script = Path.Combine(directory, "swaymsg");
        ExecutableScript.Write(script, "#!/bin/sh\nprintf started > \"$OPENXLR_FOCUS_ARGS\"\n/bin/sleep 30\n");
        using var connection = new DBusConnection("unix:path=/nonexistent");
        var focus = new DesktopFocus(new DesktopBus(connection));
        using var cancel = new CancellationTokenSource();
        Task<uint> pending = focus.ReadAsync(cancel.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!File.Exists(Path.Combine(directory, "arguments"))) await Task.Delay(10, timeout.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => focus.ReadAsync(CancellationToken.None));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        ExecutableScript.Write(script, "#!/bin/sh\nprintf '%s' \"$OPENXLR_FOCUS_REPLY\"\n");
        SetReply("sway", 44);
        Assert.Equal(44u, await focus.ReadAsync(CancellationToken.None));
    });

    [Theory]
    [InlineData("")]
    [InlineData("relative.sock")]
    [InlineData("/tmp/socket\n")]
    public Task AnInvalidSwaySocketNeverRunsTheHelper(string path) => WithHelpers(async directory =>
    {
        Environment.SetEnvironmentVariable("SWAYSOCK", path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompositorFocus.ReadAsync(FocusCompositor.Sway, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory, "arguments")));
    });

    [DesktopBusTheory]
    [InlineData("sway")]
    [InlineData("hyprland")]
    public async Task DeckAndDaemonQueriesUseTheSameProviderWithoutANewPortalOrKWinScript(string name)
    {
        await using var bus = await PrivateBus.Start();
        await WithHelpers(async directory =>
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", name);
            SetReply(name, 4242);
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            using var keys = new DesktopKeys(client);
            await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true }, save: false);
            Assert.Contains("focus routing is enabled", keys.Status);
            Assert.Equal(["4242"], await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["4242", "4343"])));
            SetReply(name, 4343);
            Assert.Equal(["4343"], await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["4242", "4343"])));
            Assert.False(File.Exists(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-focus.js")));
            File.Delete(Path.Combine(directory, name == "sway" ? "swaymsg" : "hyprctl"));
            Environment.SetEnvironmentVariable("PATH", directory + ":/usr/bin:/bin");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["4242", "4343"])));
            Assert.Contains(name == "sway" ? "swaymsg" : "hyprctl", error.Message);
        });
    }

    private static FocusCompositor Source(string name) => name == "sway" ? FocusCompositor.Sway : FocusCompositor.Hyprland;
    private static void SetReply(string name, int pid) => Environment.SetEnvironmentVariable("OPENXLR_FOCUS_REPLY",
        name == "sway" ? $"{{\"nodes\":[{{\"focused\":true,\"pid\":{pid}}}]}}" : $"{{\"pid\":{pid},\"mapped\":true,\"hidden\":false}}");

    private static async Task WithHelpers(Func<string, Task> action)
    {
        string[] names = ["PATH", "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "SWAYSOCK", "HYPRLAND_INSTANCE_SIGNATURE", "OPENXLR_FOCUS_REPLY", "OPENXLR_FOCUS_ARGS"];
        var before = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        string directory = Directory.CreateTempSubdirectory("ox-focus-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory + ":" + before["PATH"]);
            Environment.SetEnvironmentVariable("SWAYSOCK", Path.Combine(directory, "sway.sock"));
            Environment.SetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE", "test_instance");
            Environment.SetEnvironmentVariable("OPENXLR_FOCUS_ARGS", Path.Combine(directory, "arguments"));
            const string body = "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$OPENXLR_FOCUS_ARGS\"\nprintf '%s' \"$OPENXLR_FOCUS_REPLY\"\n";
            ExecutableScript.Write(Path.Combine(directory, "swaymsg"), body);
            ExecutableScript.Write(Path.Combine(directory, "hyprctl"), body);
            await action(directory);
        }
        finally
        {
            foreach (var pair in before) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Directory.Delete(directory, true);
        }
    }
}
