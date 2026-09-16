using OpenXLR.Core;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AtomicWriteTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-writes-").FullName;

    [Fact]
    public void ALeftoverTemporarySymlinkCannotOverwriteAnotherFile()
    {
        string path = Path.Combine(_directory, "settings.json");
        string other = Path.Combine(_directory, "unrelated.txt");
        File.WriteAllText(other, "keep this file");
        File.CreateSymbolicLink(path + ".tmp", other);
        OpenXlrPaths.WriteAtomic(path, "new settings");
        Assert.Equal("keep this file", File.ReadAllText(other));
        Assert.Equal("new settings", File.ReadAllText(path));
    }

    [Fact]
    public async Task ConcurrentWritersAlwaysPublishOneCompletePrivateDocument()
    {
        string path = Path.Combine(_directory, "settings.json");
        string[] documents = Enumerable.Range(0, 24).Select(i => new string((char)('A' + i), 128 * 1024)).ToArray();
        await Task.WhenAll(documents.Select(text => Task.Run(() => OpenXlrPaths.WriteAtomic(path, text))));
        Assert.Contains(File.ReadAllText(path), documents);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void AFailedPublishLeavesNoTemporaryFile()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<IOException>(() => OpenXlrPaths.WriteAtomic(path, "new settings"));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void PrivateOutputRefusesAnExistingFileOrSymbolicLink()
    {
        string path = Path.Combine(_directory, "diagnostics.tar.gz");
        string link = Path.Combine(_directory, "link.tar.gz");
        const UnixFileMode shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead;
        File.WriteAllText(path, "previous archive");
        File.SetUnixFileMode(path, shared);
        File.CreateSymbolicLink(link, path);
        Assert.ThrowsAny<IOException>(() => OpenXlrPaths.CreatePrivate(path).Dispose());
        Assert.ThrowsAny<IOException>(() => OpenXlrPaths.CreatePrivate(link).Dispose());
        Assert.Equal("previous archive", File.ReadAllText(path));
        Assert.Equal(shared, File.GetUnixFileMode(path));   // the refused file keeps its mode as well as its bytes
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
