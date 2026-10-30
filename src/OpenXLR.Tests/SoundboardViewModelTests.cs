using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The Soundboard window's model, with no window: the daemon's board shows as
/// it is and sends nothing back, a pad plays in its clip's own mode and a hold
/// pad sends its release, an unloaded clip or a board that cannot play sends
/// nothing, the meters move the bar and light the pad, the notice says why
/// the pads cannot play in the daemon's own words where it has them, imports
/// say how they went, a folder brings its sound files only, a move sends the
/// whole order, the edit window sends only what changed, and the Sounds
/// button stands on the channel the board plays into and nowhere else.
/// </summary>
[Collection("xdg-config")]
public sealed class SoundboardViewModelTests
{
    internal sealed class Recorder : ISoundboardCommands
    {
        public readonly ConcurrentQueue<(string Command, JsonNode? Value)> Sent = new();
        public string? Refuse;
        public Func<string, ClipImportResult?> Import = path => new(true, $"{Path.GetFileNameWithoutExtension(path)} was added to the soundboard.", "x");

        private Task<string?> Record(string command, object? value)
        {
            Sent.Enqueue((command, value is null ? null : JsonSerializer.SerializeToNode(value)));
            return Task.FromResult(Refuse);
        }

        public Task<string?> SetSoundboardAsync(bool enabled) => Record("setSoundboard", enabled);
        /// <summary>The page each import named, in order.</summary>
        public readonly ConcurrentQueue<string> ImportPages = new();
        public Func<string, bool, SoundboardTransferResult?> Transfer = (path, merge) => new(true, $"Done with {Path.GetFileName(path)}.");

        public Task<ClipImportResult?> ImportClipAsync(string path, string page)
        {
            Sent.Enqueue(("importClip", JsonValue.Create(path)));
            ImportPages.Enqueue(page);
            return Task.FromResult(Import(path));
        }
        public Task<SoundboardTransferResult?> ExportSoundboardAsync(string path)
        {
            Sent.Enqueue(("exportSoundboard", JsonValue.Create(path)));
            return Task.FromResult(Transfer(path, false));
        }
        public Task<SoundboardTransferResult?> ImportSoundboardAsync(string path, bool merge)
        {
            Sent.Enqueue(("importSoundboard", new JsonObject { ["path"] = path, ["merge"] = merge }));
            return Task.FromResult(Transfer(path, merge));
        }
        public Task<string?> RenameClipPageAsync(string page, string name) => Record("renameClipPage", new { page, name });
        public Task<string?> UpdateClipAsync(string clip, IReadOnlyDictionary<string, object> fields)
            => Record("updateClip", new Dictionary<string, object>(fields) { ["clip"] = clip });
        public Task<string?> DeleteClipAsync(string clip) => Record("deleteClip", clip);
        public Task<string?> SetClipOrderAsync(IReadOnlyList<string> clips) => Record("setClipOrder", clips);
        public Task<string?> PlayClipAsync(string clip) => Record("playClip", clip);
        public Task<string?> ReleaseClipAsync(string clip) => Record("releaseClip", clip);
        public Task<string?> StopClipAsync(string clip) => Record("stopClip", clip);
        public Task<string?> StopClipsAsync() => Record("stopClips", null);
        public Task<string?> AuditionClipAsync(string clip) => Record("auditionClip", clip);
        public Task<string?> StopAuditionAsync() => Record("stopAudition", null);

        public (string Command, JsonNode? Value) One()
        {
            Assert.True(Sent.TryDequeue(out var sent), "nothing was sent");
            Assert.Empty(Sent);
            return sent;
        }
    }

    /// <summary>
    /// A board with three clips: an airhorn that overlaps, a drum roll that
    /// holds, and a clip the player could not hold. <paramref name="playing"/>
    /// names the clips playing now.
    /// </summary>
    internal static JsonObject Board(bool enabled = true, bool available = true, string? channel = "sfx", string? error = null,
        string playing = "", string formats = "\"wav\",\"flac\",\"ogg\",\"opus\",\"mp3\"", bool airhornLoaded = true)
    {
        // As the daemon says it: a reason exactly when the board cannot play, and every clip off then.
        error ??= !available ? NeedsHost : channel is null ? Deleted : !enabled ? Off : null;
        string Status(string ready) => error is not null ? "off" : ready;
        string Playing(string id) => playing.Split(',').Contains(id) ? "true" : "false";
        string Text(string? text) => text is null ? "null" : JsonSerializer.Serialize(text);
        return JsonNode.Parse($$"""
            {"channel":{{Text(channel)}},"enabled":{{(enabled ? "true" : "false")}},"available":{{(available ? "true" : "false")}},
             "error":{{Text(error)}},"formats":[{{formats}}],
             "clips":[
               {"id":"airhorn","name":"Airhorn","seconds":2.4,"gain":0.8,"mode":"overlap","loop":false,"icon":"","colour":"#FF7A00",
                "loaded":{{(airhornLoaded && error is null ? "true" : "false")}},"error":null,"playing":{{Playing("airhorn")}},
                "status":"{{Status(airhornLoaded ? "ready" : "loading")}}"},
               {"id":"drumroll","name":"Drum roll","seconds":6,"gain":1,"mode":"hold","loop":true,"icon":"music","colour":null,
                "loaded":{{(error is null ? "true" : "false")}},"error":null,"playing":{{Playing("drumroll")}},"status":"{{Status("ready")}}"},
               {"id":"broken","name":"Broken","seconds":0,"gain":1,"mode":"restart","loop":false,"icon":"","colour":null,
                "loaded":false,"error":"The file could not be decoded.","playing":false,"status":"{{Status("error")}}"}],
             "playing":[{{string.Join(",", playing.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => $"\"{p}\""))}}]}
            """)!.AsObject();
    }

    internal const string NeedsHost = "The soundboard needs the OpenXLR native plugin host, which this installation does not include.";
    internal const string Deleted = "The soundboard is off because the SFX channel was deleted. Turn it on again to create SFX, muted in every mix.";
    internal const string Off = "The soundboard is off. Turn it on first.";

    private static (SoundboardViewModel Board, Recorder Commands) Model(JsonNode? board = null, IEnumerable<ChannelViewModel>? channels = null)
    {
        var commands = new Recorder();
        var model = new SoundboardViewModel(commands);
        model.Apply(board ?? Board(), channels ?? []);
        return (model, commands);
    }

    [Fact]
    public void TheDaemonsBoardShowsAsItIsAndSendsNothing()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        Assert.True(board.IsReported);
        Assert.True(board.Enabled);
        Assert.True(board.CanPlay);
        Assert.Equal(["airhorn", "drumroll", "broken"], board.Clips.Select(c => c.Id));
        SoundClipViewModel airhorn = board.Clips[0], drumroll = board.Clips[1], broken = board.Clips[2];
        Assert.Equal("Airhorn", airhorn.Name);
        Assert.Equal(0.8, airhorn.Gain);
        Assert.True(airhorn.CanPlay);
        Assert.Equal("#FF7A00", airhorn.Appearance.Colour);
        Assert.True(airhorn.Appearance.HasColour);
        // A clip without an icon of its own wears SFX's default, as its Deck key does.
        Assert.Equal("", airhorn.Appearance.Icon);
        Assert.Equal("burst", airhorn.Appearance.DefaultIcon);
        Assert.Equal("music", drumroll.Appearance.Icon);
        Assert.Equal(Localizer.Format("SoundClipDetail", 2.4, Localizer.Text("SoundClipModeOverlap")), airhorn.Detail);
        Assert.Equal(Localizer.Format("SoundClipDetailLoop", 6.0, Localizer.Text("SoundClipModeHold")), drumroll.Detail);
        Assert.True(drumroll.IsHold);
        // A clip the player does not hold says why, in the daemon's words, and cannot be pressed.
        Assert.False(broken.CanPlay);
        Assert.True(broken.HasError);
        Assert.Equal("The file could not be decoded.", broken.Detail);
        Assert.Equal(["wav", "flac", "ogg", "opus", "mp3"], board.Formats);
        Assert.False(board.HasNotice);
        Assert.Empty(commands.Sent);

        // A second state keeps the pads, follows the daemon's order and drops a deleted clip.
        JsonObject next = Board();
        JsonArray clips = next["clips"]!.AsArray();
        JsonNode first = clips[0]!;
        clips.RemoveAt(0);
        clips.RemoveAt(1);
        clips.Add(first);
        board.Apply(next, []);
        Assert.Equal(["drumroll", "airhorn"], board.Clips.Select(c => c.Id));
        Assert.Same(airhorn, board.Clips[1]);
        Assert.Same(drumroll, board.Clips[0]);
        Assert.Empty(commands.Sent);
    }

    [Fact]
    public void APadPlaysInItsClipsModeAndAHoldPadSendsItsRelease()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        SoundClipViewModel airhorn = board.Clips[0], drumroll = board.Clips[1], broken = board.Clips[2];

        board.Press(airhorn);
        Assert.Equal(("playClip", "airhorn"), Pair(commands.One()));
        // Only a hold clip's key coming up means anything to the daemon.
        board.Release(airhorn);
        Assert.Empty(commands.Sent);

        board.Press(drumroll);
        Assert.Equal(("playClip", "drumroll"), Pair(commands.One()));
        board.Release(drumroll);
        Assert.Equal(("releaseClip", "drumroll"), Pair(commands.One()));

        // A clip the player does not hold sends nothing.
        board.Press(broken);
        Assert.Empty(commands.Sent);

        board.Stop(airhorn);
        Assert.Equal(("stopClip", "airhorn"), Pair(commands.One()));
        board.StopAll();
        Assert.Equal("stopClips", commands.One().Command);

        // A board that is off, or has no channel to play into, plays nothing.
        board.Apply(Board(enabled: false), []);
        Assert.False(airhorn.CanPlay);
        board.Press(airhorn);
        board.Apply(Board(channel: null, enabled: false), []);
        board.Press(airhorn);
        Assert.Empty(commands.Sent);

        // A refusal shows under the pads, and the next command that goes through clears it.
        board.Apply(Board(), []);
        commands.Refuse = "playClip: the clip airhorn has not loaded";
        board.Press(airhorn);
        Assert.True(board.HasStatus);
        Assert.True(board.StatusWarns);
        Assert.Equal("playClip: the clip airhorn has not loaded", board.Status);
        commands.Refuse = null;
        board.Press(airhorn);
        Assert.False(board.HasStatus);
    }

    /// <summary>
    /// A pad's audition plays the clip in the monitor mixes only; the
    /// daemon's state says which clip it auditions, and the pad of that clip
    /// stops it. The pad says it is auditioned, and an audition is no
    /// press: the pad does not light.
    /// </summary>
    [Fact]
    public void APadAuditionsItsClipAndTheAuditionedPadStopsIt()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        SoundClipViewModel airhorn = board.Clips[0], drumroll = board.Clips[1], broken = board.Clips[2];
        board.Audition(airhorn);
        Assert.Equal(("auditionClip", "airhorn"), Pair(commands.One()));
        // A clip the player does not hold cannot be auditioned.
        board.Audition(broken);
        Assert.Empty(commands.Sent);

        JsonObject auditioned = Board();
        auditioned["auditioning"] = "airhorn";
        board.Apply(auditioned, []);
        Assert.True(airhorn.Auditioned);
        Assert.False(drumroll.Auditioned);
        Assert.False(airhorn.Playing);
        Assert.False(board.AnyPlaying);
        Assert.Equal(Localizer.Text("SoundClipAuditioning"), airhorn.Detail);
        Assert.Equal(Localizer.Format("SoundPadNameAuditioning", "Airhorn", Localizer.Text("SoundClipModeOverlap")), airhorn.PadName);
        board.StopAudition();
        Assert.Equal("stopAudition", commands.One().Command);
        // Another pad auditions its own clip in place of the first.
        board.Audition(drumroll);
        Assert.Equal(("auditionClip", "drumroll"), Pair(commands.One()));

        // A state without the field, from an older daemon, auditions nothing.
        board.Apply(Board(), []);
        Assert.False(airhorn.Auditioned);
        Assert.Equal(Localizer.Format("SoundClipDetail", 2.4, Localizer.Text("SoundClipModeOverlap")), airhorn.Detail);
        // A refusal shows under the pads like a press's.
        commands.Refuse = OpenXLR.Core.Mixing.Mixer.SoundboardAuditionUnheard;
        board.Audition(airhorn);
        Assert.Equal(OpenXLR.Core.Mixing.Mixer.SoundboardAuditionUnheard, board.Status);
        Assert.True(board.StatusWarns);
    }

    [Fact]
    public void TheMetersMoveTheBarAndLightThePad()
    {
        (SoundboardViewModel board, _) = Model();
        SoundClipViewModel airhorn = board.Clips[0], drumroll = board.Clips[1];
        Assert.False(board.AnyPlaying);

        board.ApplyMeters(JsonNode.Parse("""{"ch:sfx":[0.5,0.5],"clip:airhorn":[0.25,2]}""")!.AsObject());
        Assert.True(airhorn.Playing);
        Assert.Equal(0.25, airhorn.Position);
        Assert.Equal(2, airhorn.Voices);
        Assert.False(drumroll.Playing);
        Assert.True(board.AnyPlaying);
        Assert.Equal(Localizer.Format("SoundPadNamePlaying", "Airhorn", Localizer.Text("SoundClipModeOverlap")), airhorn.PadName);

        // A frame without the clip: silent again, the bar back at its start.
        board.ApplyMeters(JsonNode.Parse("""{"ch:sfx":[0,0]}""")!.AsObject());
        Assert.False(airhorn.Playing);
        Assert.Equal(0, airhorn.Position);
        Assert.False(board.AnyPlaying);

        // The state lights a pad too, before a meter frame names it.
        board.Apply(Board(playing: "drumroll"), []);
        Assert.True(drumroll.Playing);
        Assert.True(board.AnyPlaying);
    }

    [Fact]
    public void TheNoticeIsTheDaemonsOwnSentence()
    {
        // A daemon from before the soundboard.
        (SoundboardViewModel board, Recorder commands) = Model(JsonValue.Create(0));
        Assert.False(board.IsReported);
        Assert.Equal(Localizer.Text("SoundboardNotReported"), board.Notice);
        Assert.False(board.CanImport);

        // No native helper: the daemon's sentence, even while off, and nothing to add or turn on.
        board.Apply(Board(available: false, enabled: false), []);
        Assert.Equal(NeedsHost, board.Notice);
        Assert.True(board.NoticeWarns);
        Assert.False(board.CanImport);
        Assert.All(board.Clips, c => Assert.Equal("off", c.Status));

        // Off by choice: the daemon's sentence, not a warning, and the switch to turn it on.
        var sfx = new ChannelViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), "sfx", "Effects", []);
        board.Apply(Board(enabled: false), [sfx]);
        Assert.Equal(Off, board.Notice);
        Assert.False(board.NoticeWarns);
        Assert.True(board.CanImport);
        Assert.False(board.Enabled);
        Assert.Same(sfx, board.Channel);
        Assert.Equal("Effects", board.ChannelName);
        // An off pad says its length and mode; the notice says why it cannot play.
        Assert.False(board.Clips[0].CanPlay);
        Assert.Equal(Localizer.Format("SoundClipDetail", 2.4, Localizer.Text("SoundClipModeOverlap")), board.Clips[0].Detail);

        // SFX deleted: the daemon's sentence, and the switch, which creates SFX again.
        board.Apply(Board(enabled: false, channel: null), [sfx]);
        Assert.Equal(Deleted, board.Notice);
        Assert.True(board.NoticeWarns);
        Assert.False(board.Enabled);
        Assert.Null(board.Channel);
        Assert.False(board.CanPlay);
        // Clips can still be added while it is off.
        Assert.True(board.CanImport);
        Assert.Empty(commands.Sent);
        board.Enabled = true;
        Assert.Equal(("setSoundboard", "true"), Pair(commands.One()));

        // Starting: the daemon's sentence, loading pads, nothing to press.
        board.Apply(Board(error: "The soundboard is starting.", airhornLoaded: false), [sfx]);
        Assert.Equal("The soundboard is starting.", board.Notice);
        Assert.False(board.CanPlay);

        // Playing: no notice at all.
        board.Apply(Board(airhornLoaded: false), [sfx]);
        Assert.False(board.HasNotice);
        Assert.True(board.CanPlay);
        Assert.True(board.Clips[0].Loading);
        Assert.Equal(Localizer.Text("SoundClipLoading"), board.Clips[0].Detail);

        // The switch sends what the user chose; a state does not echo back.
        board.Enabled = false;
        Assert.Equal(("setSoundboard", "false"), Pair(commands.One()));
        board.Apply(Board(), [sfx]);
        Assert.Empty(commands.Sent);

        // A daemon from before the status word: worked out from loaded and error.
        JsonObject older = Board();
        foreach (JsonObject clip in older["clips"]!.AsArray().OfType<JsonObject>()) clip.Remove("status");
        board.Apply(older, [sfx]);
        Assert.Equal(["ready", "ready", "error"], board.Clips.Select(c => c.Status));
    }

    [Fact]
    public async Task ImportsSayHowTheyWent()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        await board.ImportAsync(["/home/user/Music/airhorn.mp3"]);
        Assert.Equal(("importClip", "/home/user/Music/airhorn.mp3"), Pair(commands.One()));
        Assert.Equal("airhorn was added to the soundboard.", board.Status);
        Assert.False(board.StatusWarns);

        // A refusal is the daemon's own sentence.
        commands.Import = _ => new(false, "The clip is 31.0 s long; the limit is 30 s.", null);
        await board.ImportAsync(["/home/user/Music/long.wav"]);
        commands.One();
        Assert.Equal("The clip is 31.0 s long; the limit is 30 s.", board.Status);
        Assert.True(board.StatusWarns);

        // Several: a count, then each refusal by file, and a file the daemon did not answer for.
        commands.Import = path => path.EndsWith("long.wav", StringComparison.Ordinal)
            ? new(false, "The clip is 31.0 s long; the limit is 30 s.", null)
            : path.EndsWith("lost.ogg", StringComparison.Ordinal) ? null : new(true, "added", "x");
        await board.ImportAsync(["/a/one.wav", "/a/long.wav", "/a/two.flac", "/a/lost.ogg"]);
        Assert.Equal(4, commands.Sent.Count);
        Assert.Equal(string.Join("\n", Localizer.Format("SoundboardImported", 2, 4),
            "long.wav: The clip is 31.0 s long; the limit is 30 s.", Localizer.Format("NoAnswerForPath", "lost.ogg")), board.Status);
        Assert.True(board.StatusWarns);
        Assert.False(board.Importing);

        // Without the helper, nothing is sent.
        commands.Sent.Clear();
        board.Apply(Board(available: false), []);
        await board.ImportAsync(["/a/one.wav"]);
        Assert.Empty(commands.Sent);
    }

    [Fact]
    public async Task AFolderBringsItsSoundFilesAndNothingElse()
    {
        string folder = Path.Combine(Path.GetTempPath(), "openxlr-clips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "more"));
        try
        {
            foreach (string name in new[] { "b.MP3", "a.wav", "notes.txt", "more/c.wav", "d.aiff" })
                File.WriteAllText(Path.Combine(folder, name), "");
            (SoundboardViewModel board, Recorder commands) = Model(Board(formats: "\"wav\",\"mp3\""));
            Assert.Equal([Path.Combine(folder, "a.wav"), Path.Combine(folder, "b.MP3")], board.ClipFilesIn(folder));
            await board.ImportFolderAsync(folder);
            Assert.Equal(["a.wav", "b.MP3"], commands.Sent.Select(s => Path.GetFileName(s.Value!.GetValue<string>())));
            Assert.Equal(Localizer.Format("SoundboardImported", 2, 2), board.Status);

            commands.Sent.Clear();
            await board.ImportFolderAsync(Path.Combine(folder, "more", "..", "more", "nothing-here"));
            Assert.Empty(commands.Sent);
            Assert.True(board.StatusWarns);

            Directory.CreateDirectory(Path.Combine(folder, "empty"));
            await board.ImportFolderAsync(Path.Combine(folder, "empty"));
            Assert.Equal(Localizer.Format("SoundboardFolderEmpty", "empty"), board.Status);
            Assert.Empty(commands.Sent);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AMoveSendsTheWholeOrderAndARefusalPutsTheDaemonsOrderBack()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        SoundClipViewModel airhorn = board.Clips[0], broken = board.Clips[2];
        Assert.False(board.CanMove(airhorn, -1));
        Assert.True(board.CanMove(airhorn, +1));
        Assert.False(board.CanMove(broken, +1));
        await board.Move(airhorn, +1);
        Assert.Equal(["drumroll", "airhorn", "broken"], board.Clips.Select(c => c.Id));
        (string command, JsonNode? order) = commands.One();
        Assert.Equal("setClipOrder", command);
        Assert.Equal("""["drumroll","airhorn","broken"]""", order!.ToJsonString());
        await board.Move(broken, +1);
        Assert.Empty(commands.Sent);

        // Refused: the pads go back to the order the daemon last reported, the same pads, and the refusal shows.
        commands.Refuse = "setClipOrder: the library changed";
        SoundClipViewModel drumroll = board.Clips[0];
        await board.Move(drumroll, +1);
        commands.One();
        Assert.Equal(["airhorn", "drumroll", "broken"], board.Clips.Select(c => c.Id));
        Assert.Same(drumroll, board.Clips[1]);
        Assert.Equal("setClipOrder: the library changed", board.Status);
    }

    [Fact]
    public void ADroppedErrorTakesItsWarningColourWithIt()
    {
        (SoundboardViewModel board, _) = Model();
        SoundClipViewModel broken = board.Clips[2];
        Assert.True(broken.HasError);
        Assert.Equal(1, broken.NameLines);
        Assert.Equal(3, broken.DetailLines);
        Assert.Equal("The file could not be decoded.", broken.HelpText);
        var raised = new List<string?>();
        broken.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        // SFX deleted: every clip is off, and the pad's line is no longer a warning.
        board.Apply(Board(enabled: false, channel: null), []);
        Assert.Equal("off", broken.Status);
        Assert.False(broken.HasError);
        Assert.Contains(nameof(SoundClipViewModel.HasError), raised);
        Assert.Contains(nameof(SoundClipViewModel.Detail), raised);
        Assert.Equal(2, broken.NameLines);
        Assert.Equal(1, broken.DetailLines);
        // A pad that cannot play says why to a screen reader: the board's sentence.
        Assert.Equal(Deleted, broken.HelpText);
        Assert.Equal(Deleted, board.Clips[0].HelpText);
        board.Apply(Board(), []);
        Assert.Equal("", board.Clips[0].HelpText);
    }

    [Fact]
    public async Task ASuccessfulPressClearsAPressRefusalAndNeverAnImportReport()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        SoundClipViewModel airhorn = board.Clips[0];
        commands.Import = _ => new(false, "The clip is 31.0 s long; the limit is 30 s.", null);
        await board.ImportAsync(["/a/long.wav"]);
        board.Press(airhorn);
        Assert.Equal("The clip is 31.0 s long; the limit is 30 s.", board.Status);
        Assert.True(board.StatusWarns);

        commands.Refuse = "The soundboard is starting.";
        board.Press(airhorn);
        Assert.Equal("The soundboard is starting.", board.Status);
        commands.Refuse = null;
        board.Press(airhorn);
        Assert.False(board.HasStatus);

        // A deletion's report stays through a press too.
        await board.DeleteAsync(board.Clips[1]);
        board.Press(airhorn);
        Assert.Equal(Localizer.Format("SoundClipDeleted", "Drum roll"), board.Status);
    }

    [Fact]
    public async Task AFullLibraryLeavesFilesOutAndSaysHowMany()
    {
        JsonObject full = Board();
        JsonArray clips = full["clips"]!.AsArray();
        for (int i = clips.Count; i < 62; i++)
        {
            var extra = (JsonObject)clips[0]!.DeepClone();
            extra["id"] = $"c{i}";
            clips.Add(extra);
        }
        (SoundboardViewModel board, Recorder commands) = Model(full);
        Assert.Equal(62, board.Clips.Count);
        await board.ImportAsync(["/a/1.wav", "/a/2.wav", "/a/3.wav", "/a/4.wav"], ["phone.ogg"]);
        Assert.Equal(2, commands.Sent.Count);
        Assert.Equal(string.Join("\n", Localizer.Format("SoundboardImported", 2, 2), Localizer.Format("SoundboardImportFull", 2, 64),
            Localizer.Format("SoundboardFileUnreadable", "phone.ogg")), board.Status);
        Assert.True(board.StatusWarns);
    }

    [Fact]
    public async Task ACancelStopsAnImportAfterTheFileItIsOn()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        commands.Import = path =>
        {
            if (path.EndsWith("2.wav", StringComparison.Ordinal)) board.CancelImport();
            return new(true, "added", "x");
        };
        await board.ImportAsync(["/a/1.wav", "/a/2.wav", "/a/3.wav", "/a/4.wav"]);
        Assert.Equal(2, commands.Sent.Count);
        Assert.Equal(string.Join("\n", Localizer.Format("SoundboardImported", 2, 4), Localizer.Format("SoundboardImportStopped", 2)), board.Status);
        Assert.False(board.Importing);
        // A pick the desktop gave no path for is named, even alone.
        commands.Sent.Clear();
        await board.ImportAsync([], ["phone.ogg"]);
        Assert.Empty(commands.Sent);
        Assert.Equal(Localizer.Format("SoundboardFileUnreadable", "phone.ogg"), board.Status);
    }

    [Fact]
    public async Task TheDesktopKeysHearOfTheLibraryOnlyFromAReportedBoard()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        var reports = new List<string[]>();
        var fresh = new List<bool>();
        model.Soundboard.LibraryReported += first =>
        {
            fresh.Add(first);
            reports.Add([.. model.Soundboard.Clips.Select(c => $"{c.Id}:{c.Name}:{c.Mode}")]);
        };
        string state = InputBindingModelTests.ProState(Channels, mixerExtra: ",\"soundboard\":" + Board().ToJsonString());
        Apply(model, state);
        Assert.Equal(["airhorn:Airhorn:overlap", "drumroll:Drum roll:hold", "broken:Broken:restart"], Assert.Single(reports));

        // A stopped mixer and a daemon that has gone away leave the clips as they were, and say nothing.
        JsonNode off = JsonNode.Parse(state)!;
        off["mixer"] = null;
        Apply(model, off.ToJsonString());
        model.Soundboard.Reset();
        Assert.Single(reports);

        // A daemon without a board is no library at all, not an empty one.
        Apply(model, InputBindingModelTests.ProState(Channels));
        Assert.Single(reports);
        Assert.Empty(model.Soundboard.Clips);

        // A board with every clip deleted is an empty library.
        JsonObject empty = Board();
        empty["clips"] = new JsonArray();
        Apply(model, InputBindingModelTests.ProState(Channels, mixerExtra: ",\"soundboard\":" + empty.ToJsonString()));
        Assert.Empty(reports[^1]);
        Assert.Equal(2, reports.Count);

        // Only the first report on a daemon connection is fresh: it cannot say what was deleted.
        Apply(model, state);
        model.Soundboard.Reset();
        Apply(model, state);
        Assert.Equal([true, true, false, true], fresh); // the reset before the empty board made it fresh too
    }

    [Fact]
    public async Task TheBoardSurvivesTheMixerStoppingAndTheDaemonGoingAway()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        string state = InputBindingModelTests.ProState(Channels, mixerExtra: ",\"soundboard\":" + Board().ToJsonString());
        Apply(model, state);
        SoundClipViewModel[] pads = [.. model.Soundboard.Clips];
        Assert.True(pads[0].CanPlay);

        // The software mixer stops: no mixer in the state. The pads stay, unable to play, and say why.
        JsonNode off = JsonNode.Parse(state)!;
        off["mixer"] = null;
        Apply(model, off.ToJsonString());
        Assert.Equal(pads, model.Soundboard.Clips);
        Assert.True(model.Soundboard.IsReported);
        Assert.False(model.Soundboard.MixerRunning);
        Assert.False(model.Soundboard.CanPlay);
        Assert.False(pads[0].CanPlay);
        Assert.False(model.Soundboard.ShowsMaster);
        Assert.False(model.Soundboard.CanImport);
        Assert.Equal(Localizer.Text("SoundboardMixerOff"), model.Soundboard.Notice);
        Assert.False(model.Soundboard.NoticeWarns);
        Assert.Equal(Localizer.Text("SoundboardMixerOff"), pads[0].HelpText);

        // It comes back: the same pads play again.
        Apply(model, state);
        Assert.Equal(pads, model.Soundboard.Clips);
        Assert.True(pads[0].CanPlay);
        Assert.False(model.Soundboard.HasNotice);

        // The daemon goes away: the pads stay and the notice says the daemon is not running.
        model.Soundboard.Reset();
        Assert.Equal(pads, model.Soundboard.Clips);
        Assert.False(pads[0].CanPlay);
        Assert.Equal(Localizer.Text("DaemonNotRunningSentence"), model.Soundboard.Notice);
        Apply(model, state);
        Assert.True(pads[0].CanPlay);
    }

    [Fact]
    public async Task TheEditWindowSendsOnlyWhatChanged()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        SoundClipViewModel airhorn = board.Clips[0];
        var edit = new SoundClipEditViewModel(airhorn);
        Assert.Equal("Airhorn", edit.Name);
        Assert.Equal(80, edit.GainPercent);
        Assert.Equal(0, edit.ModeIndex);
        Assert.Equal("#FF7A00", edit.Colour);
        Assert.Equal(Localizer.Format("SoundClipEditTitle", "Airhorn"), edit.Title);
        Assert.Equal(Localizer.Text("SoundClipModeOverlapHint"), edit.ModeHint);

        // Nothing changed: nothing is sent, and the window may close.
        Assert.True(await edit.SaveAsync());
        Assert.Empty(commands.Sent);

        // A gain the percent rounds, and a rename alone: only the name goes, never the rounded gain.
        JsonObject odd = Board();
        odd["clips"]![0]!["gain"] = 0.333;
        board.Apply(odd, []);
        var rename = new SoundClipEditViewModel(airhorn) { Name = "Horn" };
        Assert.Equal(33, rename.GainPercent);
        Assert.True(await rename.SaveAsync());
        Assert.Equal("""{"name":"Horn","clip":"airhorn"}""", commands.One().Value!.ToJsonString());
        // The window compares with the clip as it opened, whatever a state push changed meanwhile.
        board.Apply(Board(), []);
        Assert.True(await rename.SaveAsync());
        Assert.Equal("""{"name":"Horn","clip":"airhorn"}""", commands.One().Value!.ToJsonString());

        edit.GainPercent = 55;
        edit.ModeIndex = 3;
        edit.Loop = true;
        edit.Colour = "#ff7a00";   // the same colour in other letters is no change
        Assert.Equal(Localizer.Text("SoundClipModeHoldHint"), edit.ModeHint);
        Assert.True(await edit.SaveAsync());
        (string command, JsonNode? fields) = commands.One();
        Assert.Equal("updateClip", command);
        Assert.Equal("""{"gain":0.55,"mode":"hold","loop":true,"clip":"airhorn"}""", fields!.ToJsonString());

        // A bad name or colour stays in the window with the reason, and sends nothing.
        edit.Name = "  ";
        Assert.False(await edit.SaveAsync());
        Assert.Equal(Localizer.Text("SoundClipNameHint"), edit.Note);
        edit.Name = " Air horn ";
        edit.Colour = "orange";
        Assert.False(await edit.SaveAsync());
        Assert.Equal(Localizer.Text("ColourFormatHint"), edit.Note);
        Assert.Empty(commands.Sent);

        // Clearing the colour and renaming; the daemon's refusal stays in the window.
        edit.Colour = "";
        edit.Icon = "none";
        commands.Refuse = "updateClip: name must contain 1 to 60 printable characters";
        Assert.False(await edit.SaveAsync());
        Assert.Equal("updateClip: name must contain 1 to 60 printable characters", edit.Note);
        (_, fields) = commands.One();
        Assert.Equal("Air horn", fields!["name"]!.GetValue<string>());
        Assert.Equal("", fields["colour"]!.GetValue<string>());
        Assert.Equal("none", fields["icon"]!.GetValue<string>());
    }

    /// <summary>The board with the drum roll on Intro and the broken clip on Memes, as the daemon reports pages.</summary>
    private static JsonObject Paged(string drumroll = "Intro", string broken = "Memes")
    {
        JsonObject board = Board();
        board["clips"]![1]!["page"] = drumroll;
        board["clips"]![2]!["page"] = broken;
        board["pages"] = new JsonArray([.. new[] { "", drumroll, broken }.Distinct().Select(p => (JsonNode?)p)]);
        return board;
    }

    [Fact]
    public void PagesShowAsTabsAndThePadsAreThePageShown()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        // One page, the default one: no tabs, every pad.
        Assert.False(board.ShowsPages);
        Assert.Equal(["airhorn", "drumroll", "broken"], board.PageClips.Select(c => c.Id));

        board.Apply(Paged(), []);
        Assert.True(board.ShowsPages);
        Assert.Equal(["", "Intro", "Memes"], board.Pages.Select(p => p.Key));
        Assert.Equal([Localizer.Text("SoundboardMainPage"), "Intro", "Memes"], board.Pages.Select(p => p.Title));
        Assert.Equal(["airhorn"], board.PageClips.Select(c => c.Id));
        Assert.True(board.Pages[0].IsSelected);
        Assert.False(board.CanEditPage);

        board.ShowPage(board.Pages[1]);
        Assert.Equal("Intro", board.SelectedPage);
        Assert.Equal(["drumroll"], board.PageClips.Select(c => c.Id));
        Assert.True(board.Pages[1].IsSelected && !board.Pages[0].IsSelected);
        Assert.True(board.CanEditPage);
        Assert.Equal(Localizer.Format("SoundboardDropHint", "Intro"), board.DropHint);

        // The page renamed in another case stays shown; a page gone shows the first one.
        board.Apply(Paged(drumroll: "INTRO"), []);
        Assert.Equal("INTRO", board.SelectedPage);
        board.Apply(Paged(drumroll: "", broken: "Memes"), []);
        Assert.Equal("", board.SelectedPage);
        Assert.Equal(["airhorn", "drumroll"], board.PageClips.Select(c => c.Id));
        // The same pad stays when the page shown keeps it.
        SoundClipViewModel airhorn = board.PageClips[0];
        board.Apply(Paged(drumroll: "", broken: "Memes"), []);
        Assert.Same(airhorn, board.PageClips[0]);
        Assert.Empty(commands.Sent);

        // A daemon older than pages: every clip is on the default page.
        JsonObject older = Board();
        board.Apply(older, []);
        Assert.False(board.ShowsPages);
        Assert.Equal(3, board.PageClips.Count);
    }

    [Fact]
    public async Task AMoveTradesPlacesWithTheNeighbourOnThePage()
    {
        (SoundboardViewModel board, Recorder commands) = Model(Paged(drumroll: "Intro", broken: ""));
        SoundClipViewModel airhorn = board.Clips[0], broken = board.Clips[2];
        Assert.Equal([airhorn, broken], board.PageClips);
        Assert.True(board.CanMove(airhorn, +1));
        Assert.False(board.CanMove(broken, +1));
        await board.Move(airhorn, +1);
        Assert.Equal("""["broken","drumroll","airhorn"]""", commands.One().Value!.ToJsonString());
        Assert.Equal([broken, airhorn], board.PageClips);
    }

    [Fact]
    public async Task ImportsLandOnThePageShownAndAFolderMakesItsOwnPage()
    {
        string folder = Path.Combine(Path.GetTempPath(), "openxlr-clips-" + Guid.NewGuid().ToString("N"), " Crowd noises ");
        Directory.CreateDirectory(folder);
        string loose = Path.Combine(Path.GetDirectoryName(folder)!, "loose.wav");
        try
        {
            foreach (string name in new[] { "b.wav", "a.wav", "notes.txt" }) File.WriteAllText(Path.Combine(folder, name), "");
            File.WriteAllText(loose, "");
            (SoundboardViewModel board, Recorder commands) = Model(Paged());
            board.ShowPage(board.Pages[2]);
            await board.ImportAsync([loose]);
            Assert.Equal(["Memes"], commands.ImportPages);
            commands.ImportPages.Clear();
            commands.Sent.Clear();

            // A folder's clips go to a page named after it, printable and trimmed, and the page is shown.
            Assert.Equal("Crowd noises", SoundboardViewModel.FolderPage(folder + "/"));
            Assert.Equal("ab", SoundboardViewModel.FolderPage("/music/a\u0001b"));
            Assert.Equal("", SoundboardViewModel.FolderPage("/music/-"));
            await board.ImportFolderAsync(folder);
            Assert.Equal(["a.wav", "b.wav"], commands.Sent.Select(s => Path.GetFileName(s.Value!.GetValue<string>())));
            Assert.Equal(["Crowd noises", "Crowd noises"], commands.ImportPages);
            Assert.Equal("Crowd noises", board.SelectedPage);
            Assert.Equal(new string('x', 40), SoundboardViewModel.FolderPage("/music/" + new string('x', 50)));
            commands.ImportPages.Clear();
            commands.Sent.Clear();

            // A drop: a file onto the page shown, a folder onto its own page, all in one import.
            board.ShowPage(board.Pages[1]);
            string empty = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(folder)!, "empty")).FullName;
            await board.ImportDroppedAsync([loose, folder, empty], ["remote.wav"]);
            Assert.Equal(["loose.wav", "a.wav", "b.wav"], commands.Sent.Select(s => Path.GetFileName(s.Value!.GetValue<string>())));
            Assert.Equal(["Intro", "Crowd noises", "Crowd noises"], commands.ImportPages);
            Assert.True(board.StatusWarns);
            Assert.Contains(Localizer.Format("SoundboardImported", 3, 3), board.Status, StringComparison.Ordinal);
            Assert.Contains(Localizer.Format("SoundboardFileUnreadable", "remote.wav"), board.Status, StringComparison.Ordinal);
            Assert.Contains(Localizer.Format("SoundboardFolderEmpty", "empty"), board.Status, StringComparison.Ordinal);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public async Task APageIsRenamedOrEmptiedIntoMain()
    {
        (SoundboardViewModel board, Recorder commands) = Model(Paged());
        Assert.Null(await board.RenamePageAsync("Jingles"));
        Assert.Empty(commands.Sent);   // the default page has no name to change

        board.ShowPage(board.Pages[1]);
        Assert.Null(await board.RenamePageAsync("  Jingles "));
        Assert.Equal("""{"page":"Intro","name":"Jingles"}""", commands.One().Value!.ToJsonString());
        Assert.Equal("Jingles", board.SelectedPage);
        board.Apply(Paged(drumroll: "Jingles"), []);
        Assert.Equal(["drumroll"], board.PageClips.Select(c => c.Id));

        Assert.Null(await board.RemovePageAsync());
        Assert.Equal("""{"page":"Jingles","name":""}""", commands.One().Value!.ToJsonString());
        Assert.Equal("", board.SelectedPage);

        board.ShowPage(board.Pages[2]);
        commands.Refuse = "No clip is on the page Memes.";
        Assert.Equal("No clip is on the page Memes.", await board.RemovePageAsync());
        commands.One();
        Assert.Equal("Memes", board.SelectedPage);
        Assert.Equal("No clip is on the page Memes.", board.Status);
    }

    [Fact]
    public async Task TheLibraryGoesOutAndComesInWithItsSentence()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        Assert.True(board.CanExport);
        var running = new List<bool>();
        board.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SoundboardViewModel.Transferring)) running.Add(board.Transferring); };
        commands.Transfer = (path, merge) => new(true, "3 clips were exported to board.zip.");
        await board.ExportLibraryAsync("/home/me/board.zip");
        Assert.Equal(("exportSoundboard", "/home/me/board.zip"), (commands.Sent.Single().Command, commands.Sent.Single().Value!.GetValue<string>()));
        Assert.Equal("3 clips were exported to board.zip.", board.Status);
        Assert.False(board.StatusWarns);
        Assert.Equal([true, false], running);
        commands.Sent.Clear();

        // A partial import warns; a refusal shows the daemon's sentence; no answer says so.
        commands.Transfer = (path, merge) => new(true, "1 clip was added from the archive. Left out: b: the file is empty.", Partial: true);
        await board.ImportLibraryAsync("/home/me/board.zip", merge: true);
        Assert.Equal("""{"path":"/home/me/board.zip","merge":true}""", commands.One().Value!.ToJsonString());
        Assert.True(board.StatusWarns);
        commands.Transfer = (path, merge) => new(false, "The file is not a zip archive.");
        await board.ImportLibraryAsync("/home/me/notes.zip", merge: false);
        Assert.Equal("""{"path":"/home/me/notes.zip","merge":false}""", commands.One().Value!.ToJsonString());
        Assert.Equal("The file is not a zip archive.", board.Status);
        Assert.True(board.StatusWarns);
        commands.Transfer = (path, merge) => null;
        await board.ExportLibraryAsync("/home/me/board.zip");
        commands.One();
        Assert.Equal(Localizer.Text("SoundboardTransferNoAnswer"), board.Status);

        // A picked name without .zip is refused here, never changed, so the
        // overwrite question and a portal's grant stay for the name picked.
        await board.ExportLibraryAsync("/home/me/board");
        Assert.Empty(commands.Sent);
        Assert.Equal(Localizer.Text("SoundboardExportNeedsZip"), board.Status);
        Assert.True(board.StatusWarns);

        // Nothing to export from an empty board, and nothing while the mixer is off.
        JsonObject empty = Board();
        empty["clips"] = new JsonArray();
        board.Apply(empty, []);
        Assert.False(board.CanExport);
        await board.ExportLibraryAsync("/home/me/board.zip");
        Assert.Empty(commands.Sent);
    }

    [Fact]
    public async Task TheEditWindowMovesAClipToAPage()
    {
        (SoundboardViewModel board, Recorder commands) = Model(Paged());
        var edit = new SoundClipEditViewModel(board.Clips[1]);
        Assert.Equal("Intro", edit.Page);
        Assert.Equal(Localizer.Text("SoundboardMainPage"), edit.MainPage);
        Assert.Equal(Localizer.Format("SoundClipPageHint", Localizer.Text("SoundboardMainPage")), edit.PageHint);
        edit.Page = " Memes ";
        Assert.True(await edit.SaveAsync());
        Assert.Equal("""{"page":"Memes","clip":"drumroll"}""", commands.One().Value!.ToJsonString());
        edit.Page = "";
        Assert.True(await edit.SaveAsync());
        Assert.Equal("""{"page":"","clip":"drumroll"}""", commands.One().Value!.ToJsonString());
        edit.Page = "-";
        Assert.False(await edit.SaveAsync());
        Assert.Equal(Localizer.Text("SoundClipPageInvalid"), edit.Note);
        edit.Page = new string('p', 41);
        Assert.False(await edit.SaveAsync());
        Assert.Equal(Localizer.Text("SoundClipPageInvalid"), edit.Note);
        Assert.Empty(commands.Sent);
    }

    [Fact]
    public async Task DeletingSaysWhatHappened()
    {
        (SoundboardViewModel board, Recorder commands) = Model();
        await board.DeleteAsync(board.Clips[0]);
        Assert.Equal(("deleteClip", "airhorn"), Pair(commands.One()));
        Assert.Equal(Localizer.Format("SoundClipDeleted", "Airhorn"), board.Status);
        Assert.False(board.StatusWarns);
    }

    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse(state)!]);

    private const string Channels =
        """{"id":"xlr1","name":"XLR 1","hardware":true,"present":true},{"id":"game","name":"Game","volume":1},"""
        + """{"id":"sfx","name":"Effects","volume":0.6},{"id":"cam","name":"Camera","captureSource":"cam","volume":1}""";

    [Fact]
    public async Task TheHeaderButtonFollowsTheBoardNotTheSfxStrip()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        ChannelViewModel Channel(string id) => model.Channels.Single(c => c.Id == id);
        var raised = new List<string?>();
        model.Soundboard.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The SUBMIXER header's button shows on the board's report and lights while a clip plays.
        Apply(model, InputBindingModelTests.ProState(Channels, mixerExtra: ",\"soundboard\":" + Board(playing: "airhorn").ToJsonString()));
        Assert.True(model.HasMixer);
        Assert.True(model.Soundboard.IsReported);
        Assert.True(model.Soundboard.AnyPlaying);
        // The master is the mixer's own SFX channel: the very object the strips draw.
        Assert.Same(Channel("sfx"), model.Soundboard.Channel);
        Assert.True(model.Soundboard.HasMaster);

        // The clip ends: the button goes dark, and the binding hears it.
        raised.Clear();
        Apply(model, InputBindingModelTests.ProState(Channels, mixerExtra: ",\"soundboard\":" + Board().ToJsonString()));
        Assert.False(model.Soundboard.AnyPlaying);
        Assert.Contains(nameof(SoundboardViewModel.AnyPlaying), raised);

        // SFX deleted: the button stays, since the window's Turn on creates SFX again.
        Apply(model, InputBindingModelTests.ProState(Channels.Replace("\"sfx\"", "\"effects\""),
            mixerExtra: ",\"soundboard\":" + Board(channel: null, enabled: false).ToJsonString()));
        Assert.True(model.Soundboard.IsReported);
        Assert.False(model.Soundboard.HasMaster);

        // A daemon from before the soundboard: no button.
        Apply(model, InputBindingModelTests.ProState(Channels));
        Assert.False(model.Soundboard.IsReported);
    }

    private static (string, string?) Pair((string Command, JsonNode? Value) sent)
        => (sent.Command, sent.Value?.ToJsonString().Trim('"'));
}
