using System.Text.Json.Nodes;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PluginFolderUiTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "openxlr-folders-ui-" + Guid.NewGuid());
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public PluginFolderUiTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public void SetupRefreshReplacesFolderListAndDistinguishesSharedConfiguration()
    {
        var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        vm.ApplyPluginSetup(JsonNode.Parse("""
            {"yabridge":"5.1.1","wine":true,"bridgeProvider":"system","windowsImportDirectory":"/imports",
             "windowsDirectories":["/plugins/z","/plugins/a","/plugins/a"]}
            """));
        Assert.Equal(new[] { "/plugins/a", "/plugins/z" }, vm.WindowsDirectories);
        Assert.True(vm.HasWindowsDirectories);
        Assert.True(vm.CanManageWindows);
        Assert.True(vm.CanSyncWindows);
        Assert.True(vm.SystemBridge);
        Assert.Contains("/imports", vm.WindowsImportNote);

        vm.ApplyPluginSetup(JsonNode.Parse("""
            {"yabridge":"5.1.1","wine":false,"bridgeProvider":"openxlr","windowsDirectories":[]}
            """));
        Assert.Empty(vm.WindowsDirectories);
        Assert.False(vm.HasWindowsDirectories);
        Assert.True(vm.CanManageWindows);
        Assert.False(vm.CanSyncWindows);
        Assert.False(vm.SystemBridge);

        vm.ApplyPluginSetup(null);
        Assert.False(vm.CanManageWindows);
        Assert.False(vm.CanSyncWindows);
    }

    [Fact]
    public async Task RuntimeWarningsRefreshIndependentlyAndDisappearWhenSetupIsUnavailable()
    {
        await using var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        vm.ApplyPluginSetup(JsonNode.Parse("""
            {"windowsEditorNote":"Install the companion","memoryLockNote":"Configure memory locking"}
            """));
        Assert.True(vm.HasWindowsEditorNote);
        Assert.True(vm.HasMemoryLockNote);
        Assert.Equal("Configure memory locking", vm.MemoryLockNote);
        vm.ApplyPluginSetup(JsonNode.Parse("""{"memoryLockNote":"Configure memory locking"}"""));
        Assert.False(vm.HasWindowsEditorNote);
        Assert.True(vm.HasMemoryLockNote);
        vm.ApplyPluginSetup(JsonNode.Parse("{}"));
        Assert.False(vm.HasMemoryLockNote);
        vm.ApplyPluginSetup(JsonNode.Parse("""{"memoryLockNote":"Old warning"}"""));
        vm.ApplyPluginSetup(null);
        Assert.False(vm.HasMemoryLockNote);
        Assert.False(vm.HasWindowsEditorNote);
    }

    [Fact]
    public async Task SkippedBundlesReachOptionsThroughTheSetupProtocolAndClearOnRefresh()
    {
        await using var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        var setup = new OpenXLR.Core.Mixing.PluginSetup(true, "~/.lv2", "~/.clap", "~/.vst3", null, false, [], [])
        {
            SkippedScans = new(2, [new("vst3", "/plugins/Timeout.vst3", "timeout", "timed out", DateTimeOffset.Parse("2026-09-17T10:00:00Z")),
                new("clap", "/plugins/Old.clap", "unknown", "reason not recorded", null)]),
        };
        vm.ApplyPluginSetup(System.Text.Json.JsonSerializer.SerializeToNode(new PluginSetupMessage(setup),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Assert.Equal("Skipped after a failed scan: 2", vm.SkippedPlugins);
        Assert.Equal(2, vm.SkippedPluginDetails.Count);
        Assert.Contains("/plugins/Timeout.vst3", vm.SkippedPluginDetails[0]);
        Assert.Contains("timed out", vm.SkippedPluginDetails[0]);
        Assert.DoesNotContain("time not recorded", vm.SkippedPluginDetails[0]);
        Assert.Contains("time not recorded", vm.SkippedPluginDetails[1]);
        vm.ApplyPluginSetup(JsonNode.Parse("""{"skippedFailedCount":0,"skippedFailedBundles":[]}"""));
        Assert.Empty(vm.SkippedPluginDetails);
        Assert.Equal("Skipped after a failed scan: 0", vm.SkippedPlugins);
        vm.ApplyPluginSetup(JsonNode.Parse("{}"));
        Assert.Equal("Skipped bundles: unavailable", vm.SkippedPlugins);
        vm.ApplyPluginSetup(null);
        Assert.Empty(vm.SkippedPluginDetails);
    }

    [Fact]
    public void PluginFileReplyUsesTheFieldsTheManagerReads()
    {
        var result = new OpenXLR.Core.Mixing.WindowsPluginFiles(true, "", [
            new("/wine/EQ.vst3", "EQ.vst3", "vst3", false, false, "/wine", true),
        ]);
        string wire = System.Text.Json.JsonSerializer.Serialize(new WindowsPluginFilesMessage(result),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        JsonNode reply = JsonNode.Parse(wire)!;
        Assert.Equal("windowsPluginFiles", reply["type"]!.GetValue<string>());
        Assert.True(reply["ok"]!.GetValue<bool>());
        JsonNode row = reply["plugins"]![0]!;
        Assert.Equal("/wine/EQ.vst3", row["path"]!.GetValue<string>());
        Assert.False(row["enabled"]!.GetValue<bool>());
        Assert.False(row["canDelete"]!.GetValue<bool>());
        Assert.Equal("/wine", row["winePrefix"]!.GetValue<string>());
        Assert.True(row["inUse"]!.GetValue<bool>());
        Assert.Null(reply["result"]);
    }

    [Theory]
    [InlineData("addWindowsPluginFolder")]
    [InlineData("removeWindowsPluginFolder")]
    [InlineData("getWindowsPluginFiles")]
    [InlineData("removeWindowsPluginInserts")]
    [InlineData("setWindowsPluginEnabled")]
    [InlineData("deleteWindowsPlugin")]
    public void FolderCommandsRequireBoundedAbsolutePaths(string command)
    {
        foreach (string? path in new[] { null, "", "plugins", "~/plugins", "/plugins\nother", "/" + new string('x', 4096) })
            Assert.Contains(command, CommandValidation.CheckPluginPath(new Command { Cmd = command, Path = path }));
        Assert.Null(CommandValidation.CheckPluginPath(new Command { Cmd = command, Path = "/home/user/Windows plugins" }));
    }
}
