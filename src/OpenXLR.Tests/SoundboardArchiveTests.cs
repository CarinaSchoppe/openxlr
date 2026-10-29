using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The soundboard's library archive and its pages, against a fake probe: an
/// export reads back with the same ids, a merge makes ids unique, a replace
/// keeps them, and a hostile archive is refused before anything is unpacked
/// outside the clip directory. No audio server is involved.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SoundboardArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-soundboard-archive-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // The probe judges a file by its first four bytes, as in SoundboardPlayerTests.
    private const string FakeProbe = """
        case "$1" in
          formats) echo '{"formats":["wav","flac","mp3"]}' ;;
          probe)
            word=$(head -c 4 "$2")
            case "$word" in
              LONG) echo "probe error the clip is 31.0 s long; the limit is 30 s"; exit 1 ;;
              TEXT) echo "probe error the file is not audio this build can decode"; exit 1 ;;
              FLAC) echo "probe ok flac 1 44100 88200" ;;
              *) echo "probe ok wav 2 48000 96000" ;;
            esac ;;
        esac
        """;

    private int _boards;

    private (Mixer Mixer, List<SoundboardLibrary> Saved, string Clips) Board(SoundboardLibrary? library = null)
    {
        string probe = ExecutableScript.Write(Path.Combine(_directory, "probe"), FakeProbe);
        var mixer = new Mixer(new PipeWireAdapter { PlayerHost = probe });
        var saved = new List<SoundboardLibrary>();
        string clips = Path.Combine(_directory, $"data{++_boards}", "soundboard");
        Directory.CreateDirectory(clips);
        foreach (SoundboardClip clip in library?.Clips ?? []) File.WriteAllText(Path.Combine(clips, clip.File), "RIFF " + clip.Id);
        mixer.UseSoundboard(library ?? SoundboardLibrary.Empty, next => { saved.Add(next); return null; }, clips);
        return (mixer, saved, clips);
    }

    private string Source(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>An archive with these entries, each a name and its text, written as given.</summary>
    private string Zip(string name, params (string Entry, string Text)[] entries) => Zip(name, entries, null);

    private string Zip(string name, (string Entry, string Text)[] entries, Action<ZipArchiveEntry>? shape)
    {
        string path = Path.Combine(_directory, name);
        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            foreach ((string entry, string text) in entries)
            {
                ZipArchiveEntry created = zip.CreateEntry(entry);
                shape?.Invoke(created);
                using var into = new StreamWriter(created.Open());
                into.Write(text);
            }
        return path;
    }

    private static string Library(params string[] clips) => $$"""{"format":1,"clips":[{{string.Join(",", clips)}}]}""";

    private static string Entry(string id, string file, string page = "") => $$"""{"id":"{{id}}","name":"{{id.ToUpperInvariant()}}","file":"{{file}}","page":"{{page}}"}""";

    [Fact]
    public void AnExportImportsElsewhereWithTheSameIdsPagesAndFiles()
    {
        (Mixer mixer, _, string clips) = Board();
        using (mixer)
        {
            mixer.ImportClip(Source("Air Horn.wav", "RIFF horn"), null);
            mixer.ImportClip(Source("drum.wav", "FLAC drum"), "Drum roll", "Intro");
            mixer.ImportClip(Source("cheer.wav", "RIFF cheer"), null, "intro");
            mixer.UpdateClip("air-horn", new SoundboardClipChange(Gain: .5, Mode: "hold", Loop: true, Icon: "star", Colour: "#00FF00"));
            // A page named in another case joins the page already there.
            Assert.Equal(["Intro", "Intro"], mixer.SoundboardLibrary.Clips.Skip(1).Select(c => c.Page));

            string archive = Path.Combine(_directory, "board.zip");
            SoundboardExport export = mixer.ExportSoundboard(archive);
            Assert.Equal((3, 0), (export.Clips, export.Skipped.Count));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archive));
            // Nothing is left beside it from the staging copy.
            Assert.Equal(["board.zip"], Directory.GetFiles(_directory, "*.zip").Select(Path.GetFileName));
            Assert.Empty(Directory.GetFiles(_directory, ".openxlr-*"));
            using (ZipArchive zip = ZipFile.OpenRead(archive))
            {
                Assert.Equal(["clips/air-horn.wav", "clips/drum-roll.flac", "clips/cheer.wav", "soundboard.json"], zip.Entries.Select(e => e.FullName));
                using var reader = new StreamReader(zip.GetEntry("soundboard.json")!.Open());
                JsonNode json = JsonNode.Parse(reader.ReadToEnd())!;
                Assert.Equal(1, json["format"]!.GetValue<int>());
                Assert.Equal("Intro", json["clips"]![1]!["page"]!.GetValue<string>());
            }

            (Mixer elsewhere, List<SoundboardLibrary> saved, string otherClips) = Board();
            using (elsewhere)
            {
                SoundboardImport import = elsewhere.ImportSoundboard(archive, merge: true);
                Assert.Equal(["air-horn", "drum-roll", "cheer"], import.Added);
                Assert.Empty(import.Skipped);
                Assert.Empty(import.Renamed);
                Assert.Equal(mixer.SoundboardLibrary.Clips, elsewhere.SoundboardLibrary.Clips);
                Assert.Equal(mixer.SoundboardLibrary.Clips, saved[^1].Clips);
                Assert.Equal("FLAC drum", File.ReadAllText(Path.Combine(otherClips, "drum-roll.flac")));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(otherClips, "air-horn.wav")));
                Assert.Equal(["air-horn.wav", "cheer.wav", "drum-roll.flac"], Directory.GetFiles(otherClips).Select(Path.GetFileName).Order());
                Assert.Equal(["", "Intro"], elsewhere.Soundboard().Pages);
            }

            // An export replaces a file already there, and never writes through a folder.
            Assert.Equal(3, mixer.ExportSoundboard(archive).Clips);
            Directory.CreateDirectory(Path.Combine(_directory, "folder.zip"));
            Assert.Equal("Something other than a file is at that path.",
                Assert.Throws<InvalidOperationException>(() => mixer.ExportSoundboard(Path.Combine(_directory, "folder.zip"))).Message);
            Assert.Equal("The archive's name must end in .zip.",
                Assert.Throws<InvalidOperationException>(() => mixer.ExportSoundboard(Path.Combine(_directory, "board.tar"))).Message);
            Assert.Equal("The folder to save the archive in does not exist.",
                Assert.Throws<InvalidOperationException>(() => mixer.ExportSoundboard(Path.Combine(_directory, "absent", "b.zip"))).Message);

            // A clip whose file went missing is left out and named.
            File.Delete(Path.Combine(clips, "cheer.wav"));
            SoundboardExport partial = mixer.ExportSoundboard(Path.Combine(_directory, "partial.zip"));
            Assert.Equal(2, partial.Clips);
            Assert.Equal(["cheer: The file does not exist."], partial.Skipped);
        }
    }

    [Fact]
    public void AnEmptyBoardHasNothingToExport()
    {
        (Mixer mixer, _, _) = Board();
        using (mixer)
            Assert.Equal("The soundboard has no clips to export.",
                Assert.Throws<InvalidOperationException>(() => mixer.ExportSoundboard(Path.Combine(_directory, "empty.zip"))).Message);
        Assert.False(File.Exists(Path.Combine(_directory, "empty.zip")));
    }

    [Fact]
    public void AMergeMakesIdsUniqueAgainstClipsAndRetiredIds()
    {
        var library = new SoundboardLibrary
        {
            Clips = [new SoundboardClip { Id = "horn", Name = "Horn", File = "horn.wav", Page = "intro" }],
            Retired = ["drum"],
        };
        (Mixer mixer, List<SoundboardLibrary> saved, string clips) = Board(library);
        using (mixer)
        {
            string archive = Zip("merge.zip",
                ("soundboard.json", Library(Entry("horn", "horn.wav", "Intro"), Entry("drum", "drum.wav"), Entry("bell", "bell.wav", "INTRO"))),
                ("clips/horn.wav", "RIFF other horn"), ("clips/drum.wav", "RIFF drum"), ("clips/bell.wav", "RIFF bell"));
            SoundboardImport import = mixer.ImportSoundboard(archive, merge: true);
            Assert.False(import.Replaced);
            Assert.Equal(["horn-2", "drum-2", "bell"], import.Added);
            Assert.Equal(new Dictionary<string, string> { ["horn"] = "horn-2", ["drum"] = "drum-2" }, import.Renamed);
            Assert.Equal(["horn", "horn-2", "drum-2", "bell"], mixer.SoundboardLibrary.Clips.Select(c => c.Id));
            // The clip here keeps its file; the new one has its own.
            Assert.Equal("RIFF horn", File.ReadAllText(Path.Combine(clips, "horn.wav")));
            Assert.Equal("RIFF other horn", File.ReadAllText(Path.Combine(clips, "horn-2.wav")));
            // Pages named in another case join the page here, under its spelling.
            Assert.Equal(["intro", "intro", "", "intro"], mixer.SoundboardLibrary.Clips.Select(c => c.Page));
            Assert.Equal(["drum"], saved[^1].Retired);
        }
    }

    [Fact]
    public void AReplaceKeepsTheArchivesIdsAndDeletesTheClipsHere()
    {
        var library = new SoundboardLibrary
        {
            Clips =
            [
                new SoundboardClip { Id = "horn", Name = "Horn", File = "horn.wav" },
                new SoundboardClip { Id = "gong", Name = "Gong", File = "gong.wav" },
            ],
            Retired = ["drum", "old"],
        };
        (Mixer mixer, List<SoundboardLibrary> saved, string clips) = Board(library);
        using (mixer)
        {
            string archive = Zip("replace.zip",
                ("soundboard.json", Library(Entry("horn", "horn.wav"), Entry("drum", "d.wav", "Beats"))),
                ("clips/horn.wav", "RIFF new horn"), ("clips/d.wav", "RIFF drum"));
            SoundboardImport import = mixer.ImportSoundboard(archive, merge: false);
            Assert.True(import.Replaced);
            Assert.Equal(["horn", "drum"], import.Added);
            Assert.Empty(import.Renamed);
            Assert.Equal(["horn", "drum"], saved[^1].Clips.Select(c => c.Id));
            // A file name a clip here had is never reused, so a failed save could be undone.
            Assert.Equal(["drum.wav", "horn-2.wav"], Directory.GetFiles(clips).Select(Path.GetFileName).Order());
            Assert.Equal("RIFF new horn", File.ReadAllText(Path.Combine(clips, "horn-2.wav")));
            // The archive's ids come back from retirement; the replaced clips' ids retire.
            Assert.Equal(["old", "gong"], saved[^1].Retired);
            Assert.Equal(["", "Beats"], mixer.Soundboard().Pages);
        }
    }

    [Fact]
    public void ABadClipIsLeftOutAndTheRestImported()
    {
        (Mixer mixer, _, string clips) = Board();
        using (mixer)
        {
            string archive = Zip("partial.zip",
                ("soundboard.json", Library(Entry("good", "good.wav"), Entry("text", "text.wav"), Entry("long", "long.wav"),
                    Entry("missing", "missing.wav"), Entry("empty", "empty.wav"), """{"id":"Bad Id","name":"x","file":"x.wav"}""")),
                ("clips/good.wav", "RIFF good"), ("clips/text.wav", "TEXT notes"), ("clips/long.wav", "LONG"), ("clips/empty.wav", ""));
            SoundboardImport import = mixer.ImportSoundboard(archive, merge: true);
            Assert.Equal(["good"], import.Added);
            Assert.Equal(
            [
                "#6 (the id is not a clip id): the entry breaks the library's rules",
                "TEXT: the file is not audio this build can decode",
                "LONG: the clip is 31.0 s long; the limit is 30 s",
                "MISSING: its file is not in the archive",
                "EMPTY: the file is empty",
            ], import.Skipped);
            // Only the clip that went in is left in the clip directory.
            Assert.Equal(["good.wav"], Directory.GetFiles(clips).Select(Path.GetFileName));

            // Nothing that can be imported changes nothing.
            string none = Zip("none.zip", ("soundboard.json", Library(Entry("text", "text.wav"))), ("clips/text.wav", "TEXT"));
            Assert.Equal("No clip in the archive could be imported. TEXT: the file is not audio this build can decode.",
                Assert.Throws<InvalidOperationException>(() => mixer.ImportSoundboard(none, merge: false)).Message);
            Assert.Equal(["good"], mixer.SoundboardLibrary.Clips.Select(c => c.Id));
            Assert.Equal(["good.wav"], Directory.GetFiles(clips).Select(Path.GetFileName));
        }
    }

    [Theory]
    [InlineData("clips/../escape.wav", "The archive holds an entry a soundboard archive never has: clips/../escape.wav.")]
    [InlineData("../escape.wav", "The archive holds an entry a soundboard archive never has: ../escape.wav.")]
    [InlineData("/tmp/escape.wav", "The archive holds an entry a soundboard archive never has: /tmp/escape.wav.")]
    [InlineData("clips//escape.wav", "The archive holds an entry a soundboard archive never has: clips//escape.wav.")]
    [InlineData("clips/sub/escape.wav", "The archive holds an entry a soundboard archive never has: clips/sub/escape.wav.")]
    [InlineData("clips\\..\\escape.wav", "The archive holds an entry a soundboard archive never has: clips\\..\\escape.wav.")]
    [InlineData("clips/.hidden.wav", "The archive holds an entry a soundboard archive never has: clips/.hidden.wav.")]
    [InlineData("clips/run.sh", "The archive holds an entry a soundboard archive never has: clips/run.sh.")]
    [InlineData("readme.txt", "The archive holds an entry a soundboard archive never has: readme.txt.")]
    [InlineData("soundboard.json", "The archive holds an entry a soundboard archive never has: soundboard.json.")]
    public void AnEntryOutsideTheRulesRefusesTheWholeArchive(string entry, string error)
    {
        (Mixer mixer, _, string clips) = Board();
        using (mixer)
        {
            string archive = Zip("hostile.zip", ("soundboard.json", Library(Entry("good", "good.wav"))), ("clips/good.wav", "RIFF"), (entry, "RIFF evil"));
            Assert.Equal(error, Assert.Throws<SoundboardArchiveRefusedException>(() => mixer.ImportSoundboard(archive, merge: true)).Message);
            Assert.Empty(mixer.SoundboardLibrary.Clips);
            Assert.Empty(Directory.GetFileSystemEntries(clips));
            Assert.False(File.Exists(Path.Combine(_directory, "escape.wav")));
            Assert.False(File.Exists("/tmp/escape.wav") && File.ReadAllText("/tmp/escape.wav") == "RIFF evil");
        }
    }

    [Fact]
    public void ALinkOrASpecialFileInTheArchiveIsRefused()
    {
        (Mixer mixer, _, string clips) = Board();
        using (mixer)
        {
            // A symbolic link, as zip tools store it: the type in the high half of the external attributes.
            string archive = Zip("link.zip", [("soundboard.json", Library(Entry("link", "link.wav"))), ("clips/link.wav", "/etc/passwd")],
                entry => entry.ExternalAttributes = entry.FullName.StartsWith("clips/", StringComparison.Ordinal) ? unchecked((int)0xA1FF0000) : 0);
            Assert.Equal("The archive holds a link or a special file: clips/link.wav.",
                Assert.Throws<SoundboardArchiveRefusedException>(() => mixer.ImportSoundboard(archive, merge: true)).Message);
            Assert.Empty(Directory.GetFileSystemEntries(clips));
        }
    }

    [Fact]
    public void TooManyEntriesAreRefusedBeforeTheDirectoryIsRead()
    {
        (Mixer mixer, _, _) = Board();
        using (mixer)
        {
            (string, string)[] entries = [("soundboard.json", Library()), .. Enumerable.Range(0, 70).Select(i => ($"clips/c{i}.wav", "RIFF"))];
            string archive = Zip("many.zip", entries);
            Assert.Equal("The archive holds 71 entries; a soundboard archive holds at most 66.",
                Assert.Throws<SoundboardArchiveRefusedException>(() => mixer.ImportSoundboard(archive, merge: true)).Message);
        }
    }

    [Fact]
    public void AnEntryLargerThanAClipIsLeftOutBeforeItIsUnpacked()
    {
        (Mixer mixer, _, string clips) = Board();
        using (mixer)
        {
            string archive = Path.Combine(_directory, "large.zip");
            using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
            {
                using (var json = new StreamWriter(zip.CreateEntry("soundboard.json").Open()))
                    json.Write(Library(Entry("big", "big.wav"), Entry("small", "small.wav")));
                using (Stream big = zip.CreateEntry("clips/big.wav", CompressionLevel.Fastest).Open())
                {
                    var zeros = new byte[1 << 20];
                    for (int i = 0; i <= 64; i++) big.Write(zeros);
                }
                using (var small = new StreamWriter(zip.CreateEntry("clips/small.wav").Open())) small.Write("RIFF");
            }
            SoundboardImport import = mixer.ImportSoundboard(archive, merge: true);
            Assert.Equal(["small"], import.Added);
            Assert.Equal(["BIG: the file is 65.0 MiB; the limit is 64 MiB"], import.Skipped);
            Assert.Equal(["small.wav"], Directory.GetFiles(clips).Select(Path.GetFileName));
        }
    }

    [Fact]
    public void AnEntryThatUnpacksToMoreThanItDeclaresIsNeverWrittenPastIt()
    {
        (Mixer mixer, _, string clips) = Board();
        using (mixer)
        {
            string archive = Zip("liar.zip", ("soundboard.json", Library(Entry("liar", "liar.wav"))), ("clips/liar.wav", "RIFF" + new string('a', 4096)));
            Declare(archive, "clips/liar.wav", 8);
            // The reader stops at the declared size, and the copy is held to
            // it too: what lands is the 8 bytes declared, never the rest.
            SoundboardImport import = mixer.ImportSoundboard(archive, merge: true);
            Assert.Equal(["liar"], import.Added);
            Assert.Equal("RIFFaaaa", File.ReadAllText(Path.Combine(clips, "liar.wav")));
        }
    }

    [Fact]
    public void EntriesThatDeclareMoreThanAnArchiveHoldsAreRefusedBeforeAnyIsUnpacked()
    {
        (Mixer mixer, _, string clips) = Board();
        using (mixer)
        {
            string archive = Zip("bomb.zip", ("soundboard.json", Library(Entry("a", "a.wav"), Entry("b", "b.wav"))), ("clips/a.wav", "RIFF"), ("clips/b.wav", "RIFF"));
            Declare(archive, "clips/a.wav", 300u << 20);
            Declare(archive, "clips/b.wav", 300u << 20);
            Assert.Equal("The archive unpacks to more than 512 MiB.",
                Assert.Throws<SoundboardArchiveRefusedException>(() => mixer.ImportSoundboard(archive, merge: true)).Message);
            Assert.Empty(Directory.GetFileSystemEntries(clips));
        }
    }

    /// <summary>Rewrite an entry's uncompressed size, in its local header and in the central directory.</summary>
    private static void Declare(string archive, string entry, uint size)
    {
        byte[] bytes = File.ReadAllBytes(archive);
        byte[] name = Encoding.ASCII.GetBytes(entry);
        int patched = 0;
        for (int at = 0; at + 46 + name.Length < bytes.Length; at++)
        {
            uint signature = BitConverter.ToUInt32(bytes, at);
            if (signature == 0x04034b50 && bytes.AsSpan(at + 30, name.Length).SequenceEqual(name)) { BitConverter.TryWriteBytes(bytes.AsSpan(at + 22), size); patched++; }
            if (signature == 0x02014b50 && bytes.AsSpan(at + 46, name.Length).SequenceEqual(name)) { BitConverter.TryWriteBytes(bytes.AsSpan(at + 24), size); patched++; }
        }
        Assert.Equal(2, patched);
        File.WriteAllBytes(archive, bytes);
    }

    [Theory]
    [InlineData("{", "The archive's soundboard.json is not valid JSON")]
    [InlineData("[]", "The archive's soundboard.json lists no clips.")]
    [InlineData("""{"format":2,"clips":[]}""", "The archive was made by a newer OpenXLR. Update OpenXLR to import it.")]
    [InlineData("""{"format":1,"clips":[]}""", "The archive holds no clip to import.")]
    public void ALibraryFileThatCannotBeReadRefusesTheArchive(string json, string error)
    {
        (Mixer mixer, _, _) = Board();
        using (mixer)
        {
            string archive = Zip("json.zip", ("soundboard.json", json), ("clips/a.wav", "RIFF"));
            Assert.StartsWith(error, Assert.ThrowsAny<InvalidOperationException>(() => mixer.ImportSoundboard(archive, merge: true)).Message);
        }
    }

    [Fact]
    public void WhatIsNotASoundboardArchiveIsRefused()
    {
        (Mixer mixer, _, _) = Board();
        using (mixer)
        {
            string Refused(string path, bool merge = true) => Assert.ThrowsAny<InvalidOperationException>(() => mixer.ImportSoundboard(path, merge)).Message;
            Assert.Equal("The file is not a zip archive.", Refused(Source("notes.zip", "just some text that is not a zip")));
            Assert.Equal("The archive has no soundboard.json, so it is not a soundboard archive.", Refused(Zip("bare.zip", ("clips/a.wav", "RIFF"))));
            Assert.Equal("The file does not exist.", Refused(Path.Combine(_directory, "absent.zip")));
            Assert.Equal("The path is not a regular file.", Refused(_directory));
            Assert.Equal("Give the archive as an absolute path.", Refused("board.zip"));
            var missing = new Mixer(new PipeWireAdapter { PlayerHost = Path.Combine(_directory, "absent") });
            using (missing)
                Assert.Equal(Mixer.SoundboardNeedsHost, Assert.Throws<InvalidOperationException>(() => missing.ImportSoundboard(Path.Combine(_directory, "b.zip"), true)).Message);
        }
    }

    [Fact]
    public void AFullBoardTakesOnlyWhatFits()
    {
        var full = new SoundboardLibrary { Clips = [.. Enumerable.Range(0, 63).Select(i => new SoundboardClip { Id = $"c{i}", Name = $"C{i}", File = $"c{i}.wav" })] };
        (Mixer mixer, _, _) = Board(full);
        using (mixer)
        {
            string archive = Zip("two.zip", ("soundboard.json", Library(Entry("a", "a.wav"), Entry("b", "b.wav"))), ("clips/a.wav", "RIFF"), ("clips/b.wav", "RIFF"));
            SoundboardImport import = mixer.ImportSoundboard(archive, merge: true);
            Assert.Equal(["a"], import.Added);
            Assert.Equal(["B: the soundboard holds at most 64 clips"], import.Skipped);
            Assert.Equal("The soundboard holds 64 clips already. Delete some first, or replace the library.",
                Assert.Throws<InvalidOperationException>(() => mixer.ImportSoundboard(archive, merge: true)).Message);
            // A replace has the whole board's room.
            Assert.Equal(["a", "b"], mixer.ImportSoundboard(archive, merge: false).Added);
        }
    }

    [Fact]
    public void PagesFollowTheirClips()
    {
        var library = new SoundboardLibrary
        {
            Clips =
            [
                new SoundboardClip { Id = "a", Name = "A", File = "a.wav", Page = "Memes" },
                new SoundboardClip { Id = "b", Name = "B", File = "b.wav" },
                new SoundboardClip { Id = "c", Name = "C", File = "c.wav", Page = "Intro" },
                new SoundboardClip { Id = "d", Name = "D", File = "d.wav", Page = "Memes" },
            ],
        };
        (Mixer mixer, List<SoundboardLibrary> saved, _) = Board(library);
        using (mixer)
        {
            // The default page first, then each page where its first clip stands.
            Assert.Equal(["", "Memes", "Intro"], mixer.Soundboard().Pages);
            Assert.Equal(["Memes", "", "Intro", "Memes"], mixer.Soundboard().Clips.Select(c => c.Page));

            // A clip moved to a page goes to the end, so it is that page's last clip.
            mixer.UpdateClip("b", new SoundboardClipChange(Page: " intro "));
            Assert.Equal(["a", "c", "d", "b"], saved[^1].Clips.Select(c => c.Id));
            Assert.Equal("Intro", saved[^1].Clips[^1].Page);
            Assert.Equal(["Memes", "Intro"], mixer.Soundboard().Pages);
            // A new page comes after the others.
            mixer.UpdateClip("a", new SoundboardClipChange(Page: "Outro"));
            Assert.Equal(["Intro", "Memes", "Outro"], mixer.Soundboard().Pages);
            // A change that keeps the page keeps the place.
            mixer.UpdateClip("c", new SoundboardClipChange(Name: "Cheer", Page: "Intro"));
            Assert.Equal(["c", "d", "b", "a"], mixer.SoundboardLibrary.Clips.Select(c => c.Id));

            // A rename keeps the clips where they stand; one to another page's name joins them.
            mixer.RenameClipPage("Intro", "Jingles");
            Assert.Equal(["Jingles", "Memes", "Jingles", "Outro"], mixer.SoundboardLibrary.Clips.Select(c => c.Page));
            mixer.RenameClipPage("Outro", "memes");
            Assert.Equal(["Jingles", "Memes", "Jingles", "Memes"], mixer.SoundboardLibrary.Clips.Select(c => c.Page));
            // Only the case of a page can change.
            mixer.RenameClipPage("Memes", "MEMES");
            Assert.Equal(["Jingles", "MEMES"], mixer.Soundboard().Pages);
            // An empty name removes the page: its clips go to the default page.
            mixer.RenameClipPage("MEMES", "");
            Assert.Equal(["", "Jingles"], mixer.Soundboard().Pages);

            Assert.Equal("No clip is on the page Gone.", Assert.Throws<InvalidOperationException>(() => mixer.RenameClipPage("Gone", "x")).Message);
            Assert.Equal("A page name must have at most 40 printable characters.",
                Assert.Throws<InvalidOperationException>(() => mixer.UpdateClip("a", new SoundboardClipChange(Page: new string('p', 41)))).Message);
            Assert.Equal("A page name must have at most 40 printable characters.",
                Assert.Throws<InvalidOperationException>(() => mixer.ImportClip(Source("x.wav", "RIFF"), null, "a\tb")).Message);

            SoundboardClip imported = mixer.ImportClip(Source("y.wav", "RIFF"), null, "jingles");
            Assert.Equal("Jingles", imported.Page);
            Assert.Equal("y", mixer.SoundboardLibrary.Clips[^1].Id);
        }
    }

    [Fact]
    public void ALibraryFileKeepsPagesTrimmedAndDropsABadOne()
    {
        string path = Path.Combine(_directory, "soundboard.json");
        File.WriteAllText(path, $$"""
            {"clips": [
              {"id": "old", "name": "Old", "file": "old.wav"},
              {"id": "spaced", "name": "Spaced", "file": "spaced.wav", "page": "  Intro "},
              {"id": "control", "name": "Control", "file": "control.wav", "page": "a\u0001b"},
              {"id": "long", "name": "Long", "file": "long.wav", "page": "{{new string('p', 41)}}"}
            ]}
            """);
        SoundboardLibrary read = SoundboardLibrary.Load(path, out string? warning);
        Assert.Equal(["", "Intro"], read.Clips.Select(c => c.Page));
        Assert.Equal($"{path}: dropped control (the page must have at most 40 printable characters), long (the page must have at most 40 printable characters)", warning);
        Assert.Equal(["", "Intro"], SoundboardLibrary.PagesOf(read.Clips));
        Assert.Empty(SoundboardLibrary.PagesOf([]));
    }
}
