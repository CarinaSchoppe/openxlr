using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The daemon's side of the soundboard player and the import check, against
/// fake helpers: what lines reach the player and in what order, how its
/// answers are read, and what an import copies, checks and saves. No audio
/// server is involved.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SoundboardPlayerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-soundboard-player-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // A player that answers every load, slower for a path naming "slow",
    // refuses one naming "bad", and writes each line it is given to a file.
    private const string FakePlayer = """
        echo ready
        while IFS= read -r line; do
          printf '%s\n' "$line" >> "$0.lines"
          case "$line" in
            "load "*)
              rest=${line#load }; slot=${rest%% *}; path=${rest#* }
              case "$path" in *slow*) sleep 1 ;; esac
              case "$path" in
                *bad*) echo "clip $slot error the file is not audio this build can decode" ;;
                *) echo "clip $slot ok 96000 2" ;;
              esac ;;
            "unload "*) echo "clip ${line#unload } unloaded" ;;
            "play "*) rest=${line#play }; slot=${rest%% *}; echo "meter playing_$slot 2"; echo "meter pos_$slot 0.25" ;;
            "stop all") for s in 0 1 2 3; do echo "meter playing_$s 0"; done ;;
          esac
        done
        """;

    // A one-shot probe that judges the copy by its first word, and a
    // formats line with one name the daemon does not know.
    private const string FakeProbe = """
        case "$1" in
          formats) echo '{"formats":["wav","flac","mp3","exe"]}' ;;
          probe)
            word=$(head -c 4 "$2")
            case "$word" in
              LONG) echo "probe error the clip is 31.0 s long; the limit is 30 s"; exit 1 ;;
              TEXT) echo "probe error the file is not audio this build can decode"; exit 1 ;;
              DEAD) exit 139 ;;
              FLAC) echo "probe ok flac 1 44100 88200" ;;
              *) echo "probe ok wav 2 48000 96000" ;;
            esac ;;
        esac
        """;

    private string Script(string name, string body) => ExecutableScript.Write(Path.Combine(_directory, name), body);

    private NativePluginHost StartPlayer(out string lines)
    {
        string executable = Script("player", FakePlayer);
        lines = executable + ".body.lines";
        return new NativePluginHost(new InsertDefinition { Id = "soundboard", Kind = "player", Plugin = "player" },
            PipeWireAdapter.PlayerNode, 2, 48000, executable, [], meterSymbols: PipeWireAdapter.PlayerMeters);
    }

    private static string[] Lines(string file) => File.Exists(file) ? File.ReadAllLines(file) : [];

    private static void Until(Func<bool> done, string because)
        => Assert.True(SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10)), because);

    [Fact]
    public void ClipsLoadOneAtATimeAndPressesGoToTheirSlots()
    {
        using NativePluginHost host = StartPlayer(out string lines);
        using var player = new SoundboardPlayer(host,
            [("first", 0, "/clips/first slow.wav"), ("second", 1, "/clips/second.wav"), ("broken", 2, "/clips/bad.wav")]);
        // The second load waits for the first answer, which takes a second.
        Thread.Sleep(300);
        Assert.Equal(["load 0 /clips/first slow.wav"], Lines(lines));
        Assert.Equal("first", player.Loading);
        Assert.Equal("First is still loading.", Assert.Throws<InvalidOperationException>(() => player.Play("first", "overlap", "First")).Message);
        Until(() => player.Status("broken") is { Error: not null }, "every load answered");
        Assert.Equal(["load 0 /clips/first slow.wav", "load 1 /clips/second.wav", "load 2 /clips/bad.wav"], Lines(lines));
        Assert.Equal(new ClipLoad(true, 2, null), player.Status("first"));
        Assert.Equal(new ClipLoad(false, 0, "The file is not audio this build can decode."), player.Status("broken"));
        Assert.Null(player.Loading);
        Assert.Equal("Broken could not be loaded: The file is not audio this build can decode.",
            Assert.Throws<InvalidOperationException>(() => player.Play("broken", "overlap", "Broken")).Message);

        player.Play("second", "restart", "Second");
        Until(() => player.Progress().ContainsKey("second"), "the meters report the voice");
        Assert.Equal((0.25, 2), player.Progress()["second"]);
        player.Release("second");
        player.Stop("second");
        player.SetGain("second", .5);
        player.SetLoop("second", true);
        player.StopAll();
        Until(() => Lines(lines).Contains("stop all"), "every line arrived");
        Assert.Equal(["play 1 restart", "release 1", "stop 1", "set gain_1 0.5", "set loop_1 1", "stop all"], Lines(lines)[3..]);
        Until(() => player.Progress().Count == 0, "stop all ends the voices");
    }

    [Fact]
    public void ARemovedClipFreesItsSlotForTheNextImport()
    {
        using NativePluginHost host = StartPlayer(out string lines);
        using var player = new SoundboardPlayer(host, [("a", 0, "/clips/a.wav"), ("b", 1, "/clips/b.wav")]);
        Until(() => player.Status("b")?.Loaded == true, "both loaded");
        int version = player.Version;
        player.Remove("a");
        Assert.Null(player.SlotOf("a"));
        Assert.Null(player.Status("a"));
        Assert.True(player.Version > version);
        Assert.Equal(0, player.Add("c", "/clips/c.wav", .25, true));
        Until(() => player.Status("c")?.Loaded == true, "the first new clip loaded");
        Assert.Equal(2, player.Add("d", "/clips/d.wav", 1, false));
        Until(() => player.Status("d")?.Loaded == true, "the new clips loaded");
        Assert.Equal(["stop 0", "unload 0", "set gain_0 0.25", "set loop_0 1", "load 0 /clips/c.wav",
            "set gain_2 1", "set loop_2 0", "load 2 /clips/d.wav"], Lines(lines)[2..]);
        Assert.Throws<InvalidOperationException>(() => player.Add("c", "/clips/c.wav", 1, false));
    }

    [Fact]
    public void AClipRemovedBeforeItsTurnIsNeverLoaded()
    {
        using NativePluginHost host = StartPlayer(out string lines);
        using var player = new SoundboardPlayer(host, [("slow", 0, "/clips/slow.wav"), ("gone", 1, "/clips/gone.wav"), ("kept", 2, "/clips/kept.wav")]);
        player.Remove("gone");
        Until(() => player.Status("kept")?.Loaded == true, "the rest loaded");
        Assert.DoesNotContain("load 1 /clips/gone.wav", Lines(lines));
    }

    [Fact]
    public void ALoadThePlayerNeverAnswersFailsInTime()
    {
        string executable = Script("mute-player", "echo ready\ncat > /dev/null\n");
        using var host = new NativePluginHost(new InsertDefinition { Id = "soundboard", Kind = "player", Plugin = "player" },
            PipeWireAdapter.PlayerNode, 2, 48000, executable, [], meterSymbols: PipeWireAdapter.PlayerMeters);
        using var player = new SoundboardPlayer(host, [("a", 0, "/clips/a.wav")], loadTimeout: TimeSpan.FromMilliseconds(200));
        Until(() => player.Status("a")?.Error is not null, "the load gave up");
        Assert.Equal("The player did not load the clip in time.", player.Status("a")!.Error);
    }

    [Fact]
    public void TheProbeRunsInAProcessOfItsOwn()
    {
        var pw = new PipeWireAdapter { PlayerHost = Script("probe", FakeProbe) };
        string clip = Path.Combine(_directory, "clip.wav");
        File.WriteAllText(clip, "RIFF");
        Assert.Equal(new ClipProbe("wav", 2, 48000, 96000), pw.ProbeClip(clip));
        File.WriteAllText(clip, "LONG");
        Assert.Equal("The clip is 31.0 s long; the limit is 30 s.", Assert.Throws<InvalidOperationException>(() => pw.ProbeClip(clip)).Message);
        File.WriteAllText(clip, "DEAD");
        Assert.Equal("The file could not be checked; the decoder stopped.", Assert.Throws<InvalidOperationException>(() => pw.ProbeClip(clip)).Message);
        Assert.Equal(["wav", "flac", "mp3"], pw.PlayerFormats);
        Assert.True(pw.PlayerHostInstalled);
        var missing = new PipeWireAdapter { PlayerHost = Path.Combine(_directory, "absent") };
        Assert.False(missing.PlayerHostInstalled);
        Assert.Empty(missing.PlayerFormats);
        Assert.Equal(Mixer.SoundboardNeedsHost, Assert.Throws<InvalidOperationException>(() => missing.ProbeClip(clip)).Message);
    }

    private (Mixer Mixer, List<SoundboardLibrary> Saved, string Clips) Board(SoundboardLibrary? library = null, bool failSaves = false)
    {
        var pw = new PipeWireAdapter { PlayerHost = Script("probe", FakeProbe) };
        var mixer = new Mixer(pw);
        var saved = new List<SoundboardLibrary>();
        string clips = Path.Combine(_directory, "data", "soundboard");
        mixer.UseSoundboard(library ?? SoundboardLibrary.Empty, next =>
        {
            if (failSaves) return "the disk is full";
            saved.Add(next);
            return null;
        }, clips);
        return (mixer, saved, clips);
    }

    private string Source(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AnImportCopiesTheCheckedFileUnderAnIdOfItsOwn()
    {
        (Mixer mixer, List<SoundboardLibrary> saved, string clips) = Board();
        using (mixer)
        {
            string source = Source("Air Horn.WAV", "RIFF and more");
            SoundboardClip first = mixer.ImportClip(source, null);
            Assert.Equal(new SoundboardClip { Id = "air-horn", Name = "Air Horn", File = "air-horn.wav", Seconds = 2 }, first);
            Assert.Equal("RIFF and more", File.ReadAllText(Path.Combine(clips, "air-horn.wav")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(clips, "air-horn.wav")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(clips));
            Assert.Equal([first], saved[^1].Clips);

            // The stored extension follows what the file holds, not its name.
            SoundboardClip second = mixer.ImportClip(Source("horn.mp3", "FLAC data"), "Air horn");
            Assert.Equal(("air-horn-2", "air-horn-2.flac", 2.0), (second.Id, second.File, second.Seconds));
            // A link is followed and its target copied.
            string link = Path.Combine(_directory, "link.wav");
            File.CreateSymbolicLink(link, source);
            Assert.Equal("link", mixer.ImportClip(link, null).Id);
            Assert.Equal(["air-horn", "air-horn-2", "link"], mixer.SoundboardLibrary.Clips.Select(c => c.Id));
            // Nothing is left behind from the staging copies.
            Assert.Equal(["air-horn-2.flac", "air-horn.wav", "link.wav"], Directory.GetFiles(clips).Select(Path.GetFileName).Order());

            mixer.UpdateClip("air-horn", new SoundboardClipChange(Name: "Horn", Gain: .5, Mode: "hold", Loop: true, Icon: "star", Colour: "#00FF00"));
            Assert.Equal(new SoundboardClip { Id = "air-horn", Name = "Horn", File = "air-horn.wav", Gain = .5, Mode = "hold", Loop = true, Icon = "star", Colour = "#00FF00", Seconds = 2 },
                mixer.SoundboardLibrary.Clips[0]);
            mixer.UpdateClip("air-horn", new SoundboardClipChange(Colour: ""));
            Assert.Null(mixer.SoundboardLibrary.Clips[0].Colour);
            Assert.Equal("The gain must be from 0 to 1.",
                Assert.Throws<InvalidOperationException>(() => mixer.UpdateClip("air-horn", new SoundboardClipChange(Gain: 2))).Message);

            mixer.SetClipOrder(["link", "air-horn", "air-horn-2"]);
            Assert.Equal(["link", "air-horn", "air-horn-2"], mixer.SoundboardLibrary.Clips.Select(c => c.Id));
            Assert.Equal("Name every clip once.", Assert.Throws<InvalidOperationException>(() => mixer.SetClipOrder(["link", "link", "air-horn"])).Message);
            Assert.Equal("Name every clip once.", Assert.Throws<InvalidOperationException>(() => mixer.SetClipOrder(["link"])).Message);

            mixer.DeleteClip("air-horn");
            Assert.False(File.Exists(Path.Combine(clips, "air-horn.wav")));
            Assert.Equal(["link", "air-horn-2"], mixer.SoundboardLibrary.Clips.Select(c => c.Id));
            Assert.Equal("No clip has the id air-horn.", Assert.Throws<InvalidOperationException>(() => mixer.DeleteClip("air-horn")).Message);
            // The ids that were taken stay unique against the ones still held.
            Assert.Equal("air-horn", mixer.ImportClip(source, null).Id);
        }
    }

    [Fact]
    public void AnImportRefusesWhatIsNotAShortAudioFile()
    {
        (Mixer mixer, List<SoundboardLibrary> saved, string clips) = Board();
        using (mixer)
        {
            string Refused(string path, string? name = null)
                => Assert.Throws<InvalidOperationException>(() => mixer.ImportClip(path, name)).Message;

            Assert.Equal("Give the clip's file as an absolute path.", Refused("clip.wav"));
            Assert.Equal("The file does not exist.", Refused(Path.Combine(_directory, "absent.wav")));
            Assert.Equal("The path is not a regular file.", Refused(_directory));
            string fifo = Path.Combine(_directory, "pipe.wav");
            Assert.True(ProcessRunner.Run("mkfifo", [fifo]).Ok);
            Assert.Equal("The path is not a regular file.", Refused(fifo));
            Assert.Equal("The file is empty.", Refused(Source("empty.wav", "")));
            string large = Path.Combine(_directory, "large.wav");
            using (var stream = File.Create(large)) stream.SetLength(SoundboardLibrary.MaxFileBytes + 1);
            Assert.Equal("The file is 64.0 MiB; the limit is 64 MiB.", Refused(large));
            Assert.Equal("The clip is 31.0 s long; the limit is 30 s.", Refused(Source("long.wav", "LONG")));
            Assert.Equal("The file is not audio this build can decode.", Refused(Source("notes.txt", "TEXT")));
            Assert.Equal("The clip's name must have 1 to 60 printable characters.", Refused(Source("ok.wav", "RIFF"), new string('x', 61)));

            Assert.Empty(saved);
            Assert.Empty(mixer.SoundboardLibrary.Clips);
            // Every refusal took its staging copy with it.
            Assert.Empty(Directory.GetFileSystemEntries(clips));
        }
    }

    [Fact]
    public void AFullLibraryAndAFailedSaveLeaveNothingBehind()
    {
        var full = new SoundboardLibrary { Clips = [.. Enumerable.Range(0, 64).Select(i => new SoundboardClip { Id = $"c{i}", Name = $"C{i}", File = $"c{i}.wav" })] };
        (Mixer mixer, _, string clips) = Board(full);
        using (mixer)
            Assert.Equal("The soundboard holds 64 clips already. Delete one first.",
                Assert.Throws<InvalidOperationException>(() => mixer.ImportClip(Source("one.wav", "RIFF"), null)).Message);

        (Mixer failing, _, string failingClips) = Board(failSaves: true);
        using (failing)
        {
            Assert.Equal("The soundboard library could not be saved: the disk is full",
                Assert.Throws<IOException>(() => failing.ImportClip(Source("two.wav", "RIFF"), null)).Message);
            Assert.Empty(failing.SoundboardLibrary.Clips);
            Assert.Empty(Directory.GetFileSystemEntries(failingClips));
        }
    }

    [Fact]
    public void WithoutTheHelperTheBoardSaysSoEvenWhileOff()
    {
        var pw = new PipeWireAdapter { PlayerHost = Path.Combine(_directory, "absent") };
        using var mixer = new Mixer(pw);
        mixer.UseSoundboard(new SoundboardLibrary { Clips = [new SoundboardClip { Id = "horn", Name = "Horn", File = "horn.wav" }] }, _ => null, _directory);
        SoundboardState state = mixer.Soundboard();
        Assert.False(state.Available);
        Assert.Equal(Mixer.SoundboardNeedsHost, state.Error);
        Assert.Equal(SoundboardClipStatus.Off, state.Clips.Single().Status);
        Assert.Equal(Mixer.SoundboardNeedsHost, Assert.Throws<InvalidOperationException>(() => mixer.PlayClip("horn", null)).Message);
    }

    [Fact]
    public void PressesWhileTheBoardIsOffAreRefusedWithASentence()
    {
        var library = new SoundboardLibrary { Clips = [new SoundboardClip { Id = "horn", Name = "Horn", File = "horn.wav" }] };
        (Mixer mixer, _, _) = Board(library);
        using (mixer)
        {
            Assert.Equal(Mixer.SoundboardOff, Assert.Throws<InvalidOperationException>(() => mixer.PlayClip("horn", null)).Message);
            // A hold press with no connection to release it from never starts.
            Assert.Equal(Mixer.SoundboardHoldNeedsConnection, Assert.Throws<InvalidOperationException>(() => mixer.PlayClip("horn", "hold")).Message);
            Assert.Equal(0, mixer.ReleaseHeldClips(Guid.NewGuid()));
            Assert.Equal("No clip has the id gone.", Assert.Throws<InvalidOperationException>(() => mixer.PlayClip("gone", null)).Message);
            Assert.Equal("No clip has the id gone.", Assert.Throws<InvalidOperationException>(() => mixer.StopClip("gone")).Message);
            // Nothing plays, so stopping is no error.
            mixer.StopClips();
            mixer.StopClip("horn");
            mixer.ReleaseClip("horn");
            SoundboardState state = mixer.Soundboard();
            Assert.False(state.Enabled);
            Assert.True(state.Available);
            Assert.Equal(Mixer.SoundboardOff, state.Error);
            Assert.Equal(["wav", "flac", "mp3"], state.Formats);
            Assert.Equal(new SoundboardClipState("horn", "Horn", 0, 1, "overlap", false, "", null, false, null, false, SoundboardClipStatus.Off), Assert.Single(state.Clips));
            Assert.False(mixer.SoundboardActivityChanged());
        }
    }
}
