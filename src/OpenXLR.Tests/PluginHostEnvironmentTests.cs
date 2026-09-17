using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PluginHostEnvironmentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("plugin-environment-").FullName;
    private readonly Dictionary<string, string?> _before = new();

    private void Set(string name, string? value)
    {
        _before.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _before) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(true, false, false, null)]
    [InlineData(true, true, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(true, true, true, null)]
    [InlineData(false, true, true, "-all")]
    [InlineData(true, false, true, "+seh")]
    [InlineData(false, false, true, "")]
    [InlineData(false, false, false, "+loaddll")]
    public void ScannerAndLiveHostApplyTheirEnvironmentPolicy(bool clean, bool managed, bool trace, string? wineDebug)
    {
        Set(PluginHostEnvironment.CleanVariable, clean ? "1" : null);
        Set(PluginHostEnvironment.WineTraceVariable, trace ? "1" : null);
        Set("WINEDEBUG", wineDebug);
        Set("LD_LIBRARY_PATH", "/openxlr-test-libraries");
        Set("LD_PRELOAD", "/openxlr-test-preload.so");
        Set("LD_AUDIT", "/openxlr-test-audit.so");
        Set("OPENXLR_TEST_KEEP", "unchanged");
        Set("WINEPREFIX", Path.Combine(_root, "prefix"));
        Set("WINELOADER", "/custom/wine");
        string bridge = Path.Combine(_root, "bridge");
        if (managed)
        {
            Directory.CreateDirectory(bridge);
            foreach (string name in ManagedYabridge.RequiredFiles) File.WriteAllText(Path.Combine(bridge, name), "fixture");
            File.WriteAllText(Path.Combine(bridge, "openxlr-yabridge.json"),
                """{"formatVersion":1,"wineInputFix":true,"version":"test","sourceCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        }
        Set("OPENXLR_YABRIDGE", managed ? bridge : "system");
        Assert.Equal(managed, ManagedYabridge.Discover() is not null);
        string output = Path.Combine(_root, "environment.json");
        Set("OPENXLR_TEST_ENV_OUTPUT", output);
        string python = """
            import os, json, sys
            names = ['LD_LIBRARY_PATH', 'LD_PRELOAD', 'LD_AUDIT', 'OPENXLR_TEST_KEEP', 'WINEPREFIX', 'WINELOADER', 'PATH', 'WINEDEBUG']
            with open(os.environ['OPENXLR_TEST_ENV_OUTPUT'], 'w') as f:
                json.dump({name: os.environ.get(name) for name in names}, f)
            """;

        // The scanner's executable lives beside this test assembly. Preserve
        // an optional native build there while the fixture uses the real path.
        string scanner = NativePluginHost.Executable;
        string backup = scanner + ".environment-test-backup";
        bool existed = File.Exists(scanner);
        if (existed) File.Move(scanner, backup);
        try
        {
            string script = Path.Combine(_root, "scanner.py");
            File.WriteAllText(script, python + "\nprint('{\"plugins\":[]}')\n"
                + (trace ? "sys.stderr.write('module loads\\n' * 40000)\n" : ""));
            Set("OPENXLR_TEST_SCANNER_SCRIPT", script);
            ExecutableScript.Write(scanner, "exec /usr/bin/python3 \"$OPENXLR_TEST_SCANNER_SCRIPT\"");
            string bundles = Directory.CreateDirectory(Path.Combine(_root, "bundles")).FullName;
            File.WriteAllText(Path.Combine(bundles, "Fixture.clap"), "fixture");
            HostScan.Run("environment-test", "scan-clap", [bundles], (d, _) => Directory.GetFiles(d, "*.clap"),
                scanCache: new ScanCache(Path.Combine(_root, "cache")));
            Assert.Empty(PluginScanDiagnostics.Failures(PluginScanDiagnostics.Snapshot().Where(r => r.Kind == "environment-test")));
            JsonNode scanned = JsonNode.Parse(File.ReadAllText(output))!;
            File.Delete(output);
            using var host = new NativePluginHost(new InsertDefinition { Id = "fixture", Kind = "lv2", Plugin = "urn:fixture" },
                "fixture", 2, 48000, "/usr/bin/python3", ["-u", "-c", python + "\nprint('ready')\nfor line in sys.stdin: pass\n"]);
            JsonNode hosted = JsonNode.Parse(File.ReadAllText(output))!;
            string? inheritedDebug = wineDebug;
            Assert.Equal(inheritedDebug, Environment.GetEnvironmentVariable("WINEDEBUG"));
            Assert.Equal(inheritedDebug ?? (trace ? PluginHostEnvironment.WineTraceChannels : null), scanned["WINEDEBUG"]?.GetValue<string>());
            Assert.Equal(inheritedDebug, hosted["WINEDEBUG"]?.GetValue<string>());
            scanned.AsObject().Remove("WINEDEBUG");
            hosted.AsObject().Remove("WINEDEBUG");
            Assert.True(JsonNode.DeepEquals(scanned, hosted));
            foreach (string name in new[] { "LD_LIBRARY_PATH", "LD_PRELOAD", "LD_AUDIT" })
                Assert.Equal(clean ? null : Environment.GetEnvironmentVariable(name), hosted[name]?.GetValue<string>());
            Assert.Equal("unchanged", hosted["OPENXLR_TEST_KEEP"]!.GetValue<string>());
            Assert.Equal(Environment.GetEnvironmentVariable("WINEPREFIX"), hosted["WINEPREFIX"]!.GetValue<string>());
            Assert.Equal("/custom/wine", hosted["WINELOADER"]!.GetValue<string>());
            Assert.Equal(managed, hosted["PATH"]!.GetValue<string>().StartsWith(bridge + ":", StringComparison.Ordinal));
            Assert.Equal("/openxlr-test-preload.so", Environment.GetEnvironmentVariable("LD_PRELOAD"));
        }
        finally
        {
            File.Delete(scanner);
            File.Delete(scanner + ".body");
            if (existed) File.Move(backup, scanner);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void DiagnosticsShowBoundedEffectiveAndRemovedValues(string? choice, bool clean)
    {
        Set(PluginHostEnvironment.CleanVariable, choice);
        Set("LD_LIBRARY_PATH", "/libraries/" + new string('x', 6000));
        Set("LD_PRELOAD", "/overlays/capture.so");
        Set("LD_AUDIT", null);
        JsonNode result = JsonSerializer.SerializeToNode(new PluginHostEnvironment(null).Diagnostics())!;
        Assert.Equal(clean, result["cleanLaunch"]!.GetValue<bool>());
        var effective = result["loaderEnvironment"]!;
        var removed = result["removedLoaderEnvironment"]!.AsObject();
        Assert.Equal(clean ? 2 : 0, removed.Count);
        Assert.Null(effective["LD_AUDIT"]);
        Assert.InRange((clean ? removed : effective)["LD_LIBRARY_PATH"]!.GetValue<string>().Length, 1, PluginHostEnvironment.ValueLimit);
        if (clean) Assert.Null(effective["LD_PRELOAD"]);
        else Assert.Equal("/overlays/capture.so", effective["LD_PRELOAD"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void WineTraceIsExplicitAndDiagnosticsNameTheEffectiveScannerChannels(string? choice, bool enabled)
    {
        Set(PluginHostEnvironment.WineTraceVariable, choice);
        Set("WINEDEBUG", null);
        var scanner = new PluginHostEnvironment(null, scanner: true);
        Assert.Equal(enabled, scanner.WineTrace);
        Assert.Equal(enabled ? PluginScanLogStore.TraceCaptureBytes : PluginScanLogStore.StreamCapBytes, scanner.StderrCap);
        Assert.Equal(enabled ? PluginHostEnvironment.WineTraceChannels : null, scanner.WineDebug);
        Assert.False(new PluginHostEnvironment(null).WineTrace);
        JsonNode diagnostics = JsonSerializer.SerializeToNode(new PluginHostEnvironment(null).Diagnostics())!;
        Assert.Equal(enabled, diagnostics["wineTrace"]!.GetValue<bool>());
        Assert.Equal(scanner.WineDebug, diagnostics["scannerWineDebug"]?.GetValue<string>());
        Set("WINEDEBUG", new string('x', 5000));
        diagnostics = JsonSerializer.SerializeToNode(new PluginHostEnvironment(null).Diagnostics())!;
        Assert.Equal(PluginHostEnvironment.ValueLimit, diagnostics["scannerWineDebug"]!.GetValue<string>().Length);
    }

    [Fact]
    public void WineLoaderOverridesPathAndManagedPathIsUsedForBareRunnerNames()
    {
        string managed = Directory.CreateDirectory(Path.Combine(_root, "managed")).FullName;
        string system = Directory.CreateDirectory(Path.Combine(_root, "system")).FullName;
        ExecutableScript.Write(Path.Combine(managed, "custom-wine"), "exit 0");
        ExecutableScript.Write(Path.Combine(system, "wine"), "exit 0");
        Set("PATH", system);
        Set("WINELOADER", "custom-wine");
        var policy = new PluginHostEnvironment(new ManagedYabridge(managed, "test", new string('a', 40)));
        Assert.Equal(Path.Combine(managed, "custom-wine"), policy.WineRunner());
        Set("WINELOADER", "/a custom runner/wine");
        Assert.Equal("/a custom runner/wine", policy.WineRunner());
        Set("WINELOADER", "./runner/wine");
        Assert.Equal(Path.GetFullPath("./runner/wine"), policy.WineRunner());
        Set("WINELOADER", null);
        Assert.Equal(Path.Combine(system, "wine"), policy.WineRunner());
    }

    [Fact]
    public async Task ProcessRunnerCanRemoveInheritedVariablesInBothModes()
    {
        Set("OPENXLR_TEST_REMOVE", "inherited");
        var overlay = new Dictionary<string, string> { ["OPENXLR_TEST_REMOVE"] = "overlay", ["OPENXLR_TEST_KEEP"] = "kept" };
        ProcessResult result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "printf '%s:%s' \"${OPENXLR_TEST_REMOVE-unset}\" \"$OPENXLR_TEST_KEEP\""],
            environment: overlay, removeEnvironment: ["OPENXLR_TEST_REMOVE"]);
        Assert.True(result.Ok);
        Assert.Equal("unset:kept", result.StdoutText);
        int exit = await ProcessRunner.RunInteractiveAsync("/bin/sh", ["-c", "test -z \"${OPENXLR_TEST_REMOVE+x}\" && test \"$OPENXLR_TEST_KEEP\" = kept"],
            overlay, removeEnvironment: ["OPENXLR_TEST_REMOVE"]);
        Assert.Equal(0, exit);
        Assert.Equal("inherited", Environment.GetEnvironmentVariable("OPENXLR_TEST_REMOVE"));
    }
}
