using System.Net.Sockets;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginInstallerFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-files-" + Guid.NewGuid().ToString("N"));
    private string Installed => Path.Combine(_root, "installed");
    private PluginInstaller Installer => new(Installed, Installed, Installed, null, null);

    public PluginInstallerFileTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void SpecialFilesAreRefusedWithoutWaitingForAnotherProcess(bool bundle, bool link, bool socket)
    {
        if (!OperatingSystem.IsLinux()) return;
        string selection = Path.Combine(_root, bundle ? "gate.lv2" : "gate.clap");
        if (bundle)
        {
            Directory.CreateDirectory(selection);
            File.WriteAllText(Path.Combine(selection, "manifest.ttl"), "original");
            Assert.True(Installer.Install(selection).Ok);
            File.WriteAllText(Path.Combine(selection, "manifest.ttl"), "replacement");
        }
        string resource = bundle ? Path.Combine(selection, "resource") : selection;
        string special = link ? Path.Combine(_root, "pipe") : resource;
        using var listener = socket ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) : null;
        if (listener is not null) listener.Bind(new UnixDomainSocketEndPoint(special));
        else Assert.Equal(0, ProcessRunner.Run("mkfifo", [special], TimeSpan.FromSeconds(5)).ExitCode);
        if (link) File.CreateSymbolicLink(resource, special);

        ProcessResult result = Probe(selection);
        Assert.False(result.TimedOut, "The installer blocked while inspecting or copying a special file.");
        Assert.True(result.ExitCode == 0, result.StdoutText + result.Stderr);
        if (bundle)
        {
            Assert.Equal("original", File.ReadAllText(Path.Combine(Installed, "gate.lv2", "manifest.ttl")));
            Assert.Single(Directory.GetFileSystemEntries(Installed));
        }
        else Assert.False(Directory.Exists(Installed));
    }

    private ProcessResult Probe(string selection, string mode = "install")
    {
        // Opening a FIFO can block before a cancellation token exists. Keep
        // the real installer in a child process so a regression is killable.
        return ProcessRunner.Run("dotnet",
            ["vstest", typeof(PluginInstallerFileTests).Assembly.Location,
             "--TestCaseFilter:FullyQualifiedName=OpenXLR.Tests.PluginInstallerFileTests.PrivateInstallProbe"],
            TimeSpan.FromSeconds(15), stdoutCap: 64 * 1024, stderrCap: 16 * 1024,
            environment: new Dictionary<string, string>
            {
                ["OPENXLR_INSTALL_PROBE_SELECTION"] = selection,
                ["OPENXLR_INSTALL_PROBE_DESTINATION"] = Installed,
                ["OPENXLR_INSTALL_PROBE_MODE"] = mode,
            });
    }

    [InstallProbeFact]
    public void PrivateInstallProbe()
    {
        string? selection = Environment.GetEnvironmentVariable("OPENXLR_INSTALL_PROBE_SELECTION");
        if (selection is null) return;
        string destination = Environment.GetEnvironmentVariable("OPENXLR_INSTALL_PROBE_DESTINATION")!;
        if (Environment.GetEnvironmentVariable("OPENXLR_INSTALL_PROBE_MODE") == "registered")
        {
            var plugin = Assert.Single(PluginInstaller.RegisteredItems(selection));
            Assert.Equal("A-valid.clap", Path.GetFileName(plugin.Path));
            return;
        }
        var installer = new PluginInstaller(destination, destination, destination, null, null);
        InstallOutcome outcome = installer.Install(selection);
        Assert.False(outcome.Ok);
        Assert.Contains("regular file", outcome.Message);
    }

    private sealed class InstallProbeFactAttribute : FactAttribute
    {
        public InstallProbeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OPENXLR_INSTALL_PROBE_SELECTION") is null)
                Skip = "Runs only inside the bounded installer probe process.";
        }
    }

    [Fact]
    public void RegisteredFoldersKeepValidPluginsButANewSelectionIsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        File.WriteAllBytes(Path.Combine(_root, "A-valid.clap"), [0x7f, (byte)'E', (byte)'L', (byte)'F']);
        Assert.Equal(0, ProcessRunner.Run("mkfifo", [Path.Combine(_root, "Z-invalid.clap")], TimeSpan.FromSeconds(5)).ExitCode);
        ProcessResult listed = Probe(_root, "registered");
        Assert.False(listed.TimedOut);
        Assert.True(listed.ExitCode == 0, listed.StdoutText + listed.Stderr);
        ProcessResult picked = Probe(_root);
        Assert.False(picked.TimedOut);
        Assert.True(picked.ExitCode == 0, picked.StdoutText + picked.Stderr);
        Assert.False(Directory.Exists(Installed));
    }

    [Fact]
    public void MissingTargetsAndDirectoriesAreNotRegularFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Throws<IOException>(() => PluginInstaller.RequireRegularFile(_root));
        string missing = Path.Combine(_root, "missing");
        Assert.Contains("Could not inspect", Assert.Throws<IOException>(() => PluginInstaller.RequireRegularFile(missing)).Message);
        string link = Path.Combine(_root, "broken");
        File.CreateSymbolicLink(link, missing);
        Assert.Throws<IOException>(() => PluginInstaller.RequireRegularFile(link));
    }

    [Fact]
    public void EmptyResourcesAndSymbolicLinksRemainSupported()
    {
        if (!OperatingSystem.IsLinux()) return;
        string source = Path.Combine(_root, "gate.lv2");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "manifest.ttl"), "manifest");
        File.WriteAllBytes(Path.Combine(source, "empty"), []);
        File.CreateSymbolicLink(Path.Combine(source, "alias"), "empty");
        File.CreateSymbolicLink(Path.Combine(source, "absent-alias"), "absent");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateSymbolicLink(Path.Combine(source, "directory-alias"), "nested");
        InstallOutcome result = Installer.Install(source);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, new FileInfo(Path.Combine(Installed, "gate.lv2", "empty")).Length);
        Assert.Equal("empty", new FileInfo(Path.Combine(Installed, "gate.lv2", "alias")).LinkTarget);
        Assert.Equal("absent", new FileInfo(Path.Combine(Installed, "gate.lv2", "absent-alias")).LinkTarget);
        Assert.Equal("nested", new DirectoryInfo(Path.Combine(Installed, "gate.lv2", "directory-alias")).LinkTarget);
        Assert.True(Directory.Exists(Path.Combine(Installed, "gate.lv2", "directory-alias")));

        string module = Path.Combine(_root, "module.so");
        File.WriteAllBytes(module, [0x7f, (byte)'E', (byte)'L', (byte)'F']);
        string linked = Path.Combine(_root, "linked.clap");
        File.CreateSymbolicLink(linked, module);
        Assert.True(Installer.Install(linked).Ok);
        Assert.Equal(File.ReadAllBytes(module), File.ReadAllBytes(Path.Combine(Installed, "linked.clap")));
    }
}
