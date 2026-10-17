using System.Text;
using OpenXLR.Core;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ProfileFileTests : IDisposable
{
    private const string Device = "0fd9:007d";
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-profile-file-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private string Folder => Path.Combine(_directory, "openxlr", "profiles", "0fd9-007d");

    public ProfileFileTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        Directory.CreateDirectory(Folder);
    }

    [Theory]
    [InlineData("Large.json", MaximumBytes)]
    [InlineData("recall-on-connect", 1024)]
    public void OversizedProfileAndRecallMarkerAreRefusedWithoutChangingThem(string name, int maximum)
    {
        string path = Path.Combine(Folder, name);
        using (var file = File.Create(path)) file.SetLength(maximum + 1);
        Assert.Throws<IOException>(() => name.EndsWith(".json", StringComparison.Ordinal)
            ? (object?)ProfileStore.Load(Device, "Large") : ProfileStore.RecallOnConnect(Device));
        Assert.Equal(maximum + 1, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("../Elsewhere")]
    [InlineData("/tmp/Elsewhere")]
    [InlineData("Bad\nname")]
    public void ARecallMarkerCannotSelectAPathOrAnInvalidName(string name)
    {
        string path = Path.Combine(Folder, "recall-on-connect");
        File.WriteAllText(path, name);
        Assert.Throws<IOException>(() => ProfileStore.RecallOnConnect(Device));
        Assert.Equal(name, File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => ProfileStore.SetRecallOnConnect(Device, name));
        Assert.Equal(name, File.ReadAllText(path));
        ProfileStore.Save(Device, "Keep", new());
        Assert.Throws<IOException>(() => ProfileStore.Delete(Device, "Keep"));
        Assert.NotNull(ProfileStore.Load(Device, "Keep"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyByteOrderMarksAndUnicodeNamesStillLoad(bool utf16)
    {
        Encoding encoding = utf16 ? Encoding.Unicode : new UTF8Encoding(true);
        File.WriteAllText(Path.Combine(Folder, "Mikrofon für Gäste.json"), "{\"device\":{\"gainDb\":42}}", encoding);
        File.WriteAllText(Path.Combine(Folder, "recall-on-connect"), "Mikrofon für Gäste\r\n", encoding);
        Assert.Equal("Mikrofon für Gäste", ProfileStore.RecallOnConnect(Device));
        Assert.Equal(42, ProfileStore.Load(Device, "Mikrofon für Gäste")!.Device!.GainDb);
        Assert.Null(ProfileStore.Load(Device, "Missing"));
        File.Delete(Path.Combine(Folder, "recall-on-connect"));
        Assert.Null(ProfileStore.RecallOnConnect(Device));
    }

    [Fact]
    public void TheReadLimitIsInclusiveAndOversizedSavesKeepTheOldProfile()
    {
        string path = Path.Combine(Folder, "Boundary.json");
        string text = "{}" + new string(' ', MaximumBytes - 2);
        File.WriteAllText(path, text);
        Assert.NotNull(ProfileStore.Load(Device, "Boundary"));
        var large = new Profile { Mixer = new() { Levels = new() { [new string('x', MaximumBytes)] = 1 } } };
        Assert.Throws<IOException>(() => ProfileStore.Save(Device, "Boundary", large));
        Assert.Equal(text, File.ReadAllText(path));
        File.WriteAllText(Path.Combine(Folder, "recall-on-connect"), "Boundary" + new string(' ', 1024 - 8));
        Assert.Equal("Boundary", ProfileStore.RecallOnConnect(Device));
    }

    [Theory]
    [InlineData("Pipe.json")]
    [InlineData("recall-on-connect")]
    public async Task PipesCannotBlockProfileReads(string name)
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(Folder, name);
        var created = ProcessRunner.Run("mkfifo", [path]);
        Assert.True(created.Ok, created.Stderr);
        // Keep the pipe open at both ends so an accidental ordinary read
        // blocks. Closing this handle in finally releases that reader too.
        using var keeper = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var read = Task.Run(() => Record.Exception(() => name.EndsWith(".json", StringComparison.Ordinal)
            ? (object?)ProfileStore.Load(Device, "Pipe") : ProfileStore.RecallOnConnect(Device)));
        try { Assert.IsType<IOException>(await read.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { keeper.Dispose(); await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, recursive: true);
    }
}
