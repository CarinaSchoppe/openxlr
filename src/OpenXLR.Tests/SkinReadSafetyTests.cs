using OpenXLR.Core;
using OpenXLR.UI.Skinning;
using WindowCatalog = OpenXLR.UI.Skinning.SkinCatalog;
using TerminalCatalog = OpenXLR.Tui.SkinCatalog;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class SkinReadSafetyTests : IDisposable
{
    private const string Document = """{"schema":1,"name":"Readable","tokens":{}}""";
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-skin-read-").FullName;
    private readonly string? _oldDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _oldDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
    private string Skins => Path.Combine(_root, "openxlr", "skins");

    public SkinReadSafetyTests()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _root);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));
    }

    private string Skin(string id)
    {
        string folder = Directory.CreateDirectory(Path.Combine(Skins, id)).FullName;
        File.WriteAllText(Path.Combine(folder, "skin.json"), Document);
        return folder;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task SpecialSkinDocumentsCannotBlockDiscoveryOrAStoredReader(bool terminal, bool linked, bool listed)
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Skin("broken");
        Skin("readable");
        Func<string?>? read = listed ? TerminalCatalog.Scan().Single(entry => entry.Id == "broken").Read : null;
        string path = Path.Combine(folder, "skin.json");
        string pipe = linked ? Path.Combine(_root, "pipe") : path;
        File.Delete(path);
        Assert.True(ProcessRunner.Run("mkfifo", [pipe]).Ok);
        if (linked) File.CreateSymbolicLink(path, pipe);

        Task<Exception?> reading = Task.Run<Exception?>(() => Record.Exception(() =>
        {
            if (terminal)
            {
                read ??= TerminalCatalog.Scan().Single(entry => entry.Id == "broken").Read;
                Assert.Null(read());
                Assert.Equal("Readable", TerminalCatalog.Load("readable").Name);
            }
            else
            {
                var entries = WindowCatalog.Scan(Skins, SkinOrigin.User);
                var broken = Assert.Single(entries, entry => entry.Id == "broken");
                Assert.False(broken.Usable);
                Assert.NotEmpty(broken.Errors);
                Assert.True(Assert.Single(entries, entry => entry.Id == "readable").Usable);
            }
        }));
        try
        {
            Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(File.Exists(pipe));
        }
        finally
        {
            if (!reading.IsCompleted)
            {
                using var writer = new FileStream(pipe, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                writer.Write("{}"u8);
            }
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedSkinDocumentsRespectTheByteLimitAndLegacyEncodings(bool unicode)
    {
        string folder = Skin("linked");
        string target = Path.Combine(_root, "document.json");
        var encoding = unicode ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8;
        int padding = (SkinFormat.MaxDocumentBytes - encoding.GetPreamble().Length - encoding.GetByteCount(Document))
            / encoding.GetByteCount(" ");
        File.WriteAllText(target, Document + new string(' ', padding), encoding);
        Assert.Equal(SkinFormat.MaxDocumentBytes, new FileInfo(target).Length);
        string path = Path.Combine(folder, "skin.json");
        File.Delete(path);
        File.CreateSymbolicLink(path, target);

        Assert.True(WindowCatalog.Read(folder, "linked", SkinOrigin.User)!.Usable);
        Assert.Equal("Readable", TerminalCatalog.Load("linked").Name);

        using (var file = new FileStream(target, FileMode.Append)) file.WriteByte((byte)' ');
        Assert.False(WindowCatalog.Read(folder, "linked", SkinOrigin.User)!.Usable);
        Assert.Equal(OpenXLR.Tui.Theme.Material, TerminalCatalog.Load("linked"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _oldDataHome);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", _oldDataDirs);
        Directory.Delete(_root, recursive: true);
    }
}
