using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Collection("xdg-config")]
public sealed class SoundboardLibraryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-soundboard-library-").FullName;
    private readonly string? _dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configHome);
        Directory.Delete(_directory, recursive: true);
    }

    private static SoundboardClip Clip(string id, string? file = null) => new() { Id = id, Name = id.ToUpperInvariant(), File = file ?? id + ".wav" };

    [Fact]
    public void TheLibraryRoundTripsEveryField()
    {
        string path = Path.Combine(_directory, "soundboard.json");
        var library = new SoundboardLibrary
        {
            Enabled = true,
            Clips =
            [
                new SoundboardClip { Id = "airhorn", Name = "Airhorn", File = "airhorn.mp3", Gain = .8, Mode = "toggle", Loop = true, Icon = "star", Colour = "#FF7A00", Seconds = 2.4 },
                Clip("applause", "applause.flac"),
            ],
        };
        Assert.Null(library.Save(path));
        SoundboardLibrary read = SoundboardLibrary.Load(path, out string? warning);
        Assert.Null(warning);
        Assert.True(read.Enabled);
        Assert.Equal(library.Clips, read.Clips);
        // Private like every file under the configuration directory.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        // The names the documentation gives.
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("#FF7A00", json["clips"]![0]!["colour"]!.GetValue<string>());
        Assert.Equal("toggle", json["clips"]![0]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public void AMissingFileIsAnEmptyBoardThatIsOff()
    {
        SoundboardLibrary read = SoundboardLibrary.Load(Path.Combine(_directory, "none.json"), out string? warning);
        Assert.Null(warning);
        Assert.False(read.Enabled);
        Assert.Empty(read.Clips);
    }

    [Fact]
    public void EveryEntryThatBreaksARuleIsDroppedAndNamed()
    {
        string path = Path.Combine(_directory, "soundboard.json");
        string longName = new string('a', 252) + ".wav";
        File.WriteAllText(path, $$"""
            {"enabled": true, "clips": [
              {"id": "good", "name": "Good", "file": "good.wav"},
              {"id": "slash", "name": "Slash", "file": "a/b.wav"},
              {"id": "dots", "name": "Dots", "file": "a..b.wav"},
              {"id": "parent", "name": "Parent", "file": "../parent.wav"},
              {"id": "control", "name": "Control", "file": "a\u0001.wav"},
              {"id": "hidden", "name": "Hidden", "file": ".hidden.wav"},
              {"id": "toolong", "name": "Too long", "file": "{{longName}}"},
              {"id": "extension", "name": "Extension", "file": "script.sh"},
              {"id": "gain", "name": "Gain", "file": "gain.wav", "gain": 1.5},
              {"id": "negative", "name": "Negative", "file": "negative.wav", "gain": -0.1},
              {"id": "mode", "name": "Mode", "file": "mode.wav", "mode": "loud"},
              {"id": "icon", "name": "Icon", "file": "icon.wav", "icon": "<svg/>"},
              {"id": "colour", "name": "Colour", "file": "colour.wav", "colour": "red"},
              {"id": "Upper", "name": "Upper", "file": "upper.wav"},
              {"id": "noname", "name": "  ", "file": "noname.wav"},
              {"id": "good", "name": "Twice", "file": "twice.wav"},
              {"id": "samefile", "name": "Same file", "file": "good.wav"},
              {"name": "No id", "file": "noid.wav"},
              "not an object"
            ]}
            """);
        SoundboardLibrary read = SoundboardLibrary.Load(path, out string? warning);
        Assert.Equal(["good"], read.Clips.Select(c => c.Id));
        Assert.True(read.Enabled);
        Assert.NotNull(warning);
        foreach (string id in new[] { "slash", "dots", "parent", "control", "hidden", "toolong", "extension", "gain", "negative", "mode", "icon", "colour", "noname", "samefile" })
            Assert.Contains(id + " (", warning);
        Assert.Contains("good (the id is used twice)", warning);
        Assert.Contains("#14 (the id is not a clip id)", warning);
        Assert.Contains("#18 (", warning);
        Assert.Contains("#19 (the entry is not a clip)", warning);
    }

    [Fact]
    public void ALibraryKeepsSixtyFourClips()
    {
        string path = Path.Combine(_directory, "soundboard.json");
        var many = new SoundboardLibrary { Clips = [.. Enumerable.Range(0, 70).Select(i => Clip($"clip-{i}"))] };
        Assert.Null(many.Save(path));
        SoundboardLibrary read = SoundboardLibrary.Load(path, out string? warning);
        Assert.Equal(SoundboardLibrary.MaxClips, read.Clips.Count);
        Assert.Equal("clip-63", read.Clips[^1].Id);
        Assert.Contains("clip-64 (a library holds at most 64 clips)", warning);
    }

    [Fact]
    public void AFileThatCannotBeReadIsKeptAsACorruptCopy()
    {
        string path = Path.Combine(_directory, "soundboard.json");
        File.WriteAllText(path, "{\"enabled\": tru");
        SoundboardLibrary read = SoundboardLibrary.Load(path, out string? warning);
        Assert.Same(SoundboardLibrary.Empty, read);
        Assert.Contains("copy kept as", warning);
        Assert.Equal("{\"enabled\": tru", File.ReadAllText(path + ".corrupt"));
    }

    [Theory]
    [InlineData("Airhorn", "airhorn")]
    [InlineData("Air Horn!", "air-horn")]
    [InlineData("  Ba-dum  tss ", "ba-dum-tss")]
    [InlineData("123", "clip-123")]
    [InlineData("", "clip")]
    [InlineData("Ünïcode", "n-code")]
    public void IdsAreMadeFromNamesAsChannelIdsAre(string name, string id)
    {
        Assert.Equal(id, SoundboardLibrary.NewId(name, []));
        Assert.True(SoundboardLibrary.IsValidId(id));
    }

    [Fact]
    public void ACollidingNameGetsANumber()
    {
        Assert.Equal("airhorn-2", SoundboardLibrary.NewId("Airhorn", ["airhorn"]));
        Assert.Equal("airhorn-3", SoundboardLibrary.NewId("AIRHORN", ["airhorn", "Airhorn-2"]));
    }

    [Theory]
    [InlineData("airhorn.wav", true)]
    [InlineData("airhorn.mp3", true)]
    [InlineData("airhorn.audio", true)]
    [InlineData("airhorn.WAV", false)]
    [InlineData("airhorn", false)]
    [InlineData(".wav", false)]
    [InlineData("../airhorn.wav", false)]
    [InlineData("a/b.wav", false)]
    [InlineData("a\\b.wav", false)]
    [InlineData("a\nb.wav", false)]
    [InlineData("", false)]
    public void OnlyABareClipFileNameIsSafe(string file, bool safe) => Assert.Equal(safe, SoundboardLibrary.IsSafeFileName(file));

    [Fact]
    public void AFileNameIsMeasuredInBytes()
    {
        Assert.True(SoundboardLibrary.IsSafeFileName(new string('a', 251) + ".wav"));
        Assert.False(SoundboardLibrary.IsSafeFileName(new string('a', 252) + ".wav"));
        // Two bytes each in UTF-8.
        Assert.False(SoundboardLibrary.IsSafeFileName(new string('é', 126) + ".wav"));
    }

    [Fact]
    public void ClipsLiveUnderTheDataDirectory()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _directory);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "config"));
        Assert.Equal(Path.Combine(_directory, "openxlr", "soundboard"), SoundboardLibrary.ClipDirectory);
        Assert.Equal(Path.Combine(_directory, "config", "openxlr", "soundboard.json"), SoundboardLibrary.DefaultPath);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "openxlr"), OpenXlrPaths.DataDir);
    }
}
