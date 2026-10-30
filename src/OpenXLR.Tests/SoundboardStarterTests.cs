using System.Text.RegularExpressions;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The starter sounds: added once to a library that never had them, after
/// the clips there, never again by themselves once the library says so, and
/// restored by hand for the ones it lacks, matched by starter id. A fake
/// probe stands in for the helper, as in the archive tests; the set that
/// ships is checked against its credits, and against the real decoder when
/// the helper is built.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SoundboardStarterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-soundboard-starter-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string FakeProbe = """
        case "$1" in
          formats) echo '{"formats":["wav","flac","ogg"]}' ;;
          probe)
            word=$(head -c 4 "$2")
            case "$word" in
              TEXT) echo "probe error the file is not audio this build can decode"; exit 1 ;;
              *) echo "probe ok ogg 2 48000 96000" ;;
            esac ;;
        esac
        """;

    private static readonly string[] SetIds = ["applause", "cheer", "laugh"];

    private int _boards;

    /// <summary>A board on a library, saving into <paramref name="saved"/>; a new one on the last saved library is a restart.</summary>
    private (Mixer Mixer, string Clips) Board(List<SoundboardLibrary> saved, SoundboardLibrary? library = null, string? clips = null)
    {
        string probe = ExecutableScript.Write(Path.Combine(_directory, $"probe{++_boards}"), FakeProbe);
        var mixer = new Mixer(new PipeWireAdapter { PlayerHost = probe });
        clips ??= Path.Combine(_directory, $"data{_boards}", "soundboard");
        Directory.CreateDirectory(clips);
        foreach (SoundboardClip clip in library?.Clips ?? [])
            if (!File.Exists(Path.Combine(clips, clip.File))) File.WriteAllText(Path.Combine(clips, clip.File), "RIFF " + clip.Id);
        mixer.UseSoundboard(library ?? SoundboardLibrary.Empty, next => { saved.Add(next); return null; }, clips);
        return (mixer, clips);
    }

    /// <summary>A starter set of three clips, laid out as the packages install it.</summary>
    private string Set(params string[] leaveOut)
    {
        string set = Path.Combine(_directory, "starter-sounds");
        Directory.CreateDirectory(Path.Combine(set, "clips"));
        string entries = string.Join(",", SetIds.Select(id =>
            $$"""{"id":"{{id}}","name":"{{char.ToUpperInvariant(id[0]) + id[1..]}}","file":"{{id}}.ogg","icon":"people","page":"Starter","starter":"{{id}}"}"""));
        File.WriteAllText(Path.Combine(set, "soundboard.json"), $$"""{"format":1,"clips":[{{entries}}]}""");
        foreach (string id in SetIds.Except(leaveOut)) File.WriteAllText(Path.Combine(set, "clips", id + ".ogg"), "OggS " + id);
        return set;
    }

    private static SoundboardClip Clip(string id, string name, string page = "")
        => new() { Id = id, Name = name, File = id + ".wav", Page = page };

    [Fact]
    public void TheFirstStartAddsTheSetOnceOnItsOwnPage()
    {
        var saved = new List<SoundboardLibrary>();
        (Mixer mixer, string clips) = Board(saved);
        using (mixer)
        {
            SoundboardImport? added = mixer.AddStarterSoundsOnce(Set());
            Assert.NotNull(added);
            Assert.Equal(SetIds, added.Added);
            Assert.Empty(added.Skipped);
            SoundboardLibrary board = mixer.SoundboardLibrary;
            Assert.True(board.StarterAdded);
            Assert.All(board.Clips, c => Assert.Equal("Starter", c.Page));
            Assert.Equal(SetIds, board.Clips.Select(c => c.Starter));
            Assert.Equal("people", board.Clips[0].Icon);
            Assert.Equal("OggS cheer", File.ReadAllText(Path.Combine(clips, "cheer.ogg")));
            // The clips and the mark go in one save.
            Assert.Single(saved);
            Assert.True(saved[0].StarterAdded);
            Assert.Equal(["Starter"], mixer.Soundboard().Pages);

            Assert.Null(mixer.AddStarterSoundsOnce(Set()));
            Assert.Single(saved);
        }

        // The next start finds the mark and adds nothing.
        (Mixer restarted, _) = Board(saved, saved[^1], clips);
        using (restarted)
        {
            Assert.Null(restarted.AddStarterSoundsOnce(Set()));
            Assert.Equal(3, restarted.SoundboardLibrary.Clips.Count);
            Assert.Single(saved);
        }
    }

    [Fact]
    public void AnExistingBoardKeepsItsClipsAndGetsTheSetAfterThem()
    {
        var saved = new List<SoundboardLibrary>();
        // A clip of the user's already holds the id applause, and another the page name in another case.
        var library = new SoundboardLibrary { Clips = [Clip("applause", "My applause"), Clip("horn", "Horn", "starter")] };
        (Mixer mixer, string clips) = Board(saved, library);
        using (mixer)
        {
            SoundboardImport added = mixer.AddStarterSoundsOnce(Set())!;
            Assert.Equal(["applause-2", "cheer", "laugh"], added.Added);
            Assert.Equal("applause-2", added.Renamed["applause"]);
            SoundboardLibrary board = mixer.SoundboardLibrary;
            Assert.Equal(library.Clips, board.Clips.Take(2));
            Assert.Equal("RIFF applause", File.ReadAllText(Path.Combine(clips, "applause.wav")));
            Assert.Equal(["", "", "applause", "cheer", "laugh"], board.Clips.Select(c => c.Starter));
            // The set joins the page already there under its spelling.
            Assert.All(board.Clips.Skip(1), c => Assert.Equal("starter", c.Page));
            Assert.True(board.StarterAdded);
        }
    }

    [Fact]
    public void DeletedStarterClipsStayDeletedUntilRestored()
    {
        var saved = new List<SoundboardLibrary>();
        (Mixer mixer, string clips) = Board(saved);
        using (mixer)
        {
            mixer.AddStarterSoundsOnce(Set());
            mixer.DeleteClip("cheer");
        }
        (Mixer restarted, _) = Board(saved, saved[^1], clips);
        using (restarted)
        {
            Assert.Null(restarted.AddStarterSoundsOnce(Set()));
            Assert.Equal(["applause", "laugh"], restarted.SoundboardLibrary.Clips.Select(c => c.Id));

            // Restoring adds back the one missing; a deleted clip's id is never given again.
            SoundboardImport restored = restarted.RestoreStarterSounds(Set());
            Assert.Equal(["cheer-2"], restored.Added);
            Assert.Equal(["applause", "laugh", "cheer-2"], restarted.SoundboardLibrary.Clips.Select(c => c.Id));
            Assert.Equal("cheer", restarted.SoundboardLibrary.Clips[^1].Starter);

            // Nothing is missing now: nothing is added, nothing is saved.
            int saves = saved.Count;
            SoundboardImport again = restarted.RestoreStarterSounds(Set());
            Assert.Empty(again.Added);
            Assert.Empty(again.Skipped);
            Assert.Equal(saves, saved.Count);
        }
    }

    [Fact]
    public void ARenamedOrMovedStarterClipCountsAsPresent()
    {
        var saved = new List<SoundboardLibrary>();
        (Mixer mixer, _) = Board(saved);
        using (mixer)
        {
            mixer.AddStarterSoundsOnce(Set());
            mixer.UpdateClip("laugh", new SoundboardClipChange(Name: "Giggle", Page: ""));
            Assert.Empty(mixer.RestoreStarterSounds(Set()).Added);
            Assert.Equal(["Applause", "Cheer", "Giggle"], mixer.SoundboardLibrary.Clips.Select(c => c.Name));
        }
    }

    [Fact]
    public void AMissingSetChangesNothingAndIsTriedAgainNextStart()
    {
        var saved = new List<SoundboardLibrary>();
        var library = new SoundboardLibrary { Clips = [Clip("horn", "Horn")] };
        (Mixer mixer, _) = Board(saved, library);
        using (mixer)
        {
            var ex = Assert.Throws<StarterSoundsMissingException>(() => mixer.AddStarterSoundsOnce(Path.Combine(_directory, "absent")));
            Assert.StartsWith("The starter sounds are not installed:", ex.Message);
            Assert.Throws<StarterSoundsMissingException>(() => mixer.RestoreStarterSounds(Path.Combine(_directory, "absent")));
            Assert.Empty(saved);
            Assert.False(mixer.SoundboardLibrary.StarterAdded);
            Assert.Equal(library.Clips, mixer.SoundboardLibrary.Clips);

            // A later start with the set installed adds it.
            Assert.Equal(SetIds, mixer.AddStarterSoundsOnce(Set())!.Added);
        }
    }

    [Fact]
    public void AClipFileMissingFromTheSetIsLeftOutAndNamed()
    {
        var saved = new List<SoundboardLibrary>();
        (Mixer mixer, _) = Board(saved);
        using (mixer)
        {
            SoundboardImport added = mixer.AddStarterSoundsOnce(Set("cheer"))!;
            Assert.Equal(["applause", "laugh"], added.Added);
            Assert.Equal(["Cheer: the file does not exist"], added.Skipped);
            Assert.True(mixer.SoundboardLibrary.StarterAdded);
        }
    }

    [Fact]
    public void ASetWhoseClipsAreAllMissingIsRefusedAndNotMarked()
    {
        var saved = new List<SoundboardLibrary>();
        (Mixer mixer, _) = Board(saved);
        using (mixer)
        {
            var ex = Assert.Throws<SoundboardImportRefusedException>(() => mixer.AddStarterSoundsOnce(Set(SetIds)));
            Assert.Equal(3, ex.Skipped.Count);
            Assert.Empty(saved);
            Assert.False(mixer.SoundboardLibrary.StarterAdded);
        }
    }

    [Fact]
    public void ABoardThatHasEveryStarterClipIsMarkedWithoutAddingAny()
    {
        var saved = new List<SoundboardLibrary>();
        // As an archive from a board that had them would leave it.
        var library = new SoundboardLibrary { Clips = [.. SetIds.Select(id => Clip(id, id) with { Starter = id })] };
        (Mixer mixer, _) = Board(saved, library);
        using (mixer)
        {
            SoundboardImport added = mixer.AddStarterSoundsOnce(Set())!;
            Assert.Empty(added.Added);
            Assert.True(Assert.Single(saved).StarterAdded);
            Assert.Equal(library.Clips, mixer.SoundboardLibrary.Clips);
        }
    }

    [Fact]
    public void WithoutTheHelperTheSetIsRefused()
    {
        var mixer = new Mixer(new PipeWireAdapter { PlayerHost = Path.Combine(_directory, "absent") });
        using (mixer)
        {
            Assert.Equal(Mixer.SoundboardNeedsHost,
                Assert.Throws<InvalidOperationException>(() => mixer.AddStarterSoundsOnce(Set())).Message);
            Assert.False(mixer.SoundboardLibrary.StarterAdded);
        }
    }

    [Fact]
    public void TheMarkAndTheStarterIdsAreKeptInTheLibraryFile()
    {
        string path = Path.Combine(_directory, "soundboard.json");
        var library = new SoundboardLibrary { StarterAdded = true, Clips = [Clip("cheer", "Cheer") with { Starter = "cheer" }, Clip("horn", "Horn")] };
        Assert.Null(library.Save(path));
        SoundboardLibrary read = SoundboardLibrary.Load(path, out string? warning);
        Assert.Null(warning);
        Assert.True(read.StarterAdded);
        Assert.Equal(["cheer", ""], read.Clips.Select(c => c.Starter));

        // A file from before the starter sounds has neither.
        File.WriteAllText(path, """{"enabled":true,"clips":[{"id":"horn","name":"Horn","file":"horn.wav"}]}""");
        read = SoundboardLibrary.Load(path, out warning);
        Assert.Null(warning);
        Assert.False(read.StarterAdded);
        Assert.Equal("", read.Clips[0].Starter);

        // A starter id of the wrong shape breaks the rules.
        File.WriteAllText(path, """{"clips":[{"id":"horn","name":"Horn","file":"horn.wav","starter":"Not An Id"}]}""");
        read = SoundboardLibrary.Load(path, out warning);
        Assert.Empty(read.Clips);
        Assert.Contains("the starter id is not a clip id", warning);
    }

    // --- the set that ships --------------------------------------------------

    private static List<SoundboardClip> Shipped()
    {
        var skipped = new List<string>();
        List<SoundboardClip> clips = StarterSounds.Read(StarterSounds.Directory, skipped);
        Assert.Empty(skipped);
        return clips;
    }

    [Fact]
    public void EveryShippedClipHasItsCreditsAndItsFile()
    {
        List<SoundboardClip> clips = Shipped();
        Assert.Equal(18, clips.Count);
        Assert.All(clips, c =>
        {
            Assert.Equal("Starter", c.Page);
            Assert.Equal(c.Id, c.Starter);
            Assert.Equal(c.Id + ".ogg", c.File);
        });
        Assert.Equal(clips.Count, clips.Select(c => c.Starter).Distinct().Count());

        string[] files = [.. Directory.GetFiles(Path.Combine(StarterSounds.Directory, "clips")).Select(Path.GetFileName).Order()!];
        Assert.Equal(clips.Select(c => c.File).Order(), files);

        // CREDITS has one entry per clip, each with a source, an author and the licence, and none for anything else.
        string credits = File.ReadAllText(Path.Combine(StarterSounds.Directory, "CREDITS"));
        string[] entries = [.. Regex.Matches(credits, @"^([a-z0-9-]+\.ogg): (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value + "|" + m.Groups[2].Value)];
        Assert.Equal(clips.Select(c => c.File + "|" + c.Name), entries);
        foreach (string block in credits.Split("\n\n").Where(b => b.Contains(".ogg: ", StringComparison.Ordinal)))
        {
            Assert.Matches(@"\n  URL: https://", block);
            Assert.Matches(@"\n  Author: \S", block);
            Assert.Contains("\n  Licence: CC0 1.0\n", block + "\n");
            Assert.Matches(@"\n  Edits: \S", block);
        }
        long total = files.Sum(f => new FileInfo(Path.Combine(StarterSounds.Directory, "clips", f)).Length);
        Assert.InRange(total, 1, 1536 * 1024);
    }

    [StarterHostFact]
    public void EveryShippedClipDecodesInTheHelper()
    {
        var pw = new PipeWireAdapter { PlayerHost = NativePluginHost.Executable };
        foreach (SoundboardClip clip in Shipped())
        {
            ClipProbe probe = pw.ProbeClip(Path.Combine(StarterSounds.Directory, "clips", clip.File));
            Assert.Equal("ogg", probe.Extension);
            Assert.InRange(probe.Seconds, .4, 8);
        }
    }
}

/// <summary>Runs where the build put the native helper beside the tests (-p:EnableNativeLv2Host=true).</summary>
internal sealed class StarterHostFactAttribute : FactAttribute
{
    public StarterHostFactAttribute()
    {
        if (!File.Exists(NativePluginHost.Executable)) Skip = "Build with -p:EnableNativeLv2Host=true to check the starter sounds with the helper.";
    }
}
