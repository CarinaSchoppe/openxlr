using System.Text;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class ManagedYabridgeSafetyTests : IDisposable
{
    private const int MaximumReceiptBytes = 64 * 1024;
    private readonly string _root = Directory.CreateTempSubdirectory("bridge-safety-").FullName;
    private static readonly byte[] Receipt = JsonSerializer.SerializeToUtf8Bytes(new
    {
        formatVersion = 1, version = "5.1.1.54", sourceCommit = new string('a', 40), wineInputFix = true,
    });

    private string Package(string name)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (string file in ManagedYabridge.RequiredFiles) File.WriteAllText(Path.Combine(folder, file), "fixture");
        File.WriteAllBytes(Path.Combine(folder, "openxlr-yabridge.json"), Receipt);
        return folder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReceiptPipeDoesNotBlockDiscoveryOfTheNextPackage(bool linked)
    {
        if (!OperatingSystem.IsLinux()) return;
        string broken = Package("broken"), usable = Package("usable");
        string path = Path.Combine(broken, "openxlr-yabridge.json");
        string pipe = linked ? Path.Combine(_root, "pipe") : path;
        File.Delete(path);
        Assert.True(ProcessRunner.Run("mkfifo", [pipe]).Ok);
        if (linked) File.CreateSymbolicLink(path, pipe);
        Task<Exception?> reading = Task.Run<Exception?>(() => Record.Exception(() =>
        {
            Assert.Equal(usable, ManagedYabridge.Find(null, [broken, usable])?.Directory);
            Assert.Null(ManagedYabridge.Find(broken, [usable]));
        }));
        try { Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally
        {
            if (!reading.IsCompleted)
            {
                using var writer = new FileStream(pipe, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                writer.Write(Receipt);
            }
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(File.Exists(pipe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptByteLimitsAreInclusiveAndAlsoApplyToLinks(bool linked)
    {
        string package = Package("package");
        string path = Path.Combine(package, "openxlr-yabridge.json");
        string target = linked ? Path.Combine(_root, "receipt.json") : path;
        File.WriteAllText(target, Encoding.UTF8.GetString(Receipt).PadRight(MaximumReceiptBytes), new UTF8Encoding(false));
        if (linked)
        {
            File.Delete(path);
            File.CreateSymbolicLink(path, target);
        }
        Assert.Equal(package, ManagedYabridge.Find(null, [package])?.Directory);

        using (var file = new FileStream(target, FileMode.Append)) file.WriteByte((byte)' ');
        Assert.Null(ManagedYabridge.Find(null, [package]));
        Assert.Equal(MaximumReceiptBytes + 1, new FileInfo(target).Length);
    }

    [Theory]
    [InlineData("yabridgectl", false)]
    [InlineData("libyabridge-vst3.so", false)]
    [InlineData("yabridge-host.exe", true)]
    public void SpecialPayloadFilesDoNotQualifyAsACompletePackage(string file, bool linked)
    {
        if (!OperatingSystem.IsLinux()) return;
        string broken = Package("broken"), usable = Package("usable");
        string path = Path.Combine(broken, file);
        string pipe = linked ? Path.Combine(_root, "pipe") : path;
        File.Delete(path);
        Assert.True(ProcessRunner.Run("mkfifo", [pipe]).Ok);
        if (linked) File.CreateSymbolicLink(path, pipe);

        Assert.Equal(usable, ManagedYabridge.Find(null, [broken, usable])?.Directory);
        Assert.Null(ManagedYabridge.Find(broken, [usable]));
        Assert.True(File.Exists(pipe));
    }

    [Fact]
    public void PayloadLinksToRegularFilesRemainSupported()
    {
        string package = Package("package");
        string target = Path.Combine(_root, "payload");
        File.WriteAllText(target, "fixture");
        foreach (string file in ManagedYabridge.RequiredFiles)
        {
            string path = Path.Combine(package, file);
            File.Delete(path);
            File.CreateSymbolicLink(path, target);
        }
        Assert.Equal(package, ManagedYabridge.Find(null, [package])?.Directory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
