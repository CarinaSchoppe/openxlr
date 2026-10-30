using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class SoundboardValidationTests
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? Check(string json, bool host = true)
    {
        Command command = JsonSerializer.Deserialize<Command>(json, Wire)!;
        using var mixer = new Mixer();
        return CommandValidation.Check(command, mixer, _ => null, nativeHostInstalled: host);
    }

    [Theory]
    [InlineData("""{"cmd":"setSoundboard","enabled":true}""")]
    [InlineData("""{"cmd":"setSoundboard","enabled":false}""")]
    [InlineData("""{"cmd":"importClip","path":"/home/me/airhorn.mp3"}""")]
    [InlineData("""{"cmd":"importClip","path":"/home/me/air horn.mp3","name":"Air horn"}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","name":"Horn"}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","gain":0}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","gain":1,"mode":"hold","loop":true}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","icon":"star","colour":"#ff7a00"}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","colour":""}""")]
    [InlineData("""{"cmd":"deleteClip","clip":"airhorn"}""")]
    [InlineData("""{"cmd":"playClip","clip":"airhorn"}""")]
    [InlineData("""{"cmd":"playClip","clip":"airhorn-2","mode":"restart"}""")]
    [InlineData("""{"cmd":"releaseClip","clip":"airhorn"}""")]
    [InlineData("""{"cmd":"stopClip","clip":"airhorn"}""")]
    [InlineData("""{"cmd":"stopClips"}""")]
    [InlineData("""{"cmd":"auditionClip","clip":"airhorn"}""")]
    [InlineData("""{"cmd":"stopAudition"}""")]
    [InlineData("""{"cmd":"setClipOrder","clips":["b","a"]}""")]
    [InlineData("""{"cmd":"setClipOrder","clips":[]}""")]
    [InlineData("""{"cmd":"getSoundboard"}""")]
    [InlineData("""{"cmd":"importClip","path":"/home/me/a.wav","page":"Intro"}""")]
    [InlineData("""{"cmd":"importClip","path":"/home/me/a.wav","page":""}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","page":"Memes"}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","page":" Memes "}""")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","page":""}""")]
    [InlineData("""{"cmd":"renameClipPage","page":"Memes","name":"Jokes"}""")]
    [InlineData("""{"cmd":"renameClipPage","page":"","name":"Intro"}""")]
    [InlineData("""{"cmd":"renameClipPage","page":"Memes","name":""}""")]
    [InlineData("""{"cmd":"exportSoundboard","path":"/home/me/board.zip"}""")]
    [InlineData("""{"cmd":"exportSoundboard","path":"/home/me/Board.ZIP"}""")]
    [InlineData("""{"cmd":"importSoundboard","path":"/home/me/board.zip","merge":true}""")]
    [InlineData("""{"cmd":"importSoundboard","path":"/home/me/board.zip","merge":false}""")]
    [InlineData("""{"cmd":"restoreStarterSounds"}""")]
    public void WellFormedCommandsPass(string json) => Assert.Null(Check(json));

    [Theory]
    [InlineData("""{"cmd":"setSoundboard"}""", "setSoundboard: need 'enabled', a boolean")]
    [InlineData("""{"cmd":"importClip"}""", "importClip: need 'path', an absolute path to the file")]
    [InlineData("""{"cmd":"importClip","path":"airhorn.mp3"}""", "importClip: need 'path', an absolute path to the file")]
    [InlineData("""{"cmd":"importClip","path":"/tmp/a\nb.mp3"}""", "importClip: need 'path', an absolute path to the file")]
    [InlineData("""{"cmd":"importClip","path":"/tmp/a.mp3","name":""}""", "importClip: name must contain 1 to 60 printable characters")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn"}""", "updateClip: give at least one of name, gain, mode, loop, icon, colour and page")]
    [InlineData("""{"cmd":"updateClip","name":"Horn"}""", "updateClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"updateClip","clip":"../etc","name":"Horn"}""", "updateClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","gain":1.01}""", "updateClip: gain must be from 0 to 1")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","gain":-1}""", "updateClip: gain must be from 0 to 1")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","mode":"loop"}""", "updateClip: mode must be overlap, restart, toggle or hold")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","colour":"orange"}""", "updateClip: colour must be #RRGGBB, or empty to clear it")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","name":"   "}""", "updateClip: name must contain 1 to 60 printable characters")]
    [InlineData("""{"cmd":"playClip"}""", "playClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"playClip","clip":"Airhorn"}""", "playClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"playClip","clip":"airhorn","mode":"once"}""", "playClip: mode must be overlap, restart, toggle or hold")]
    [InlineData("""{"cmd":"deleteClip"}""", "deleteClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"releaseClip","clip":""}""", "releaseClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"stopClip","clip":"a b"}""", "stopClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"auditionClip"}""", "auditionClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"auditionClip","clip":"Airhorn"}""", "auditionClip: need 'clip', a clip id")]
    [InlineData("""{"cmd":"setClipOrder"}""", "setClipOrder: need 'clips', at most 64 clip ids")]
    [InlineData("""{"cmd":"setClipOrder","clips":["a","B"]}""", "setClipOrder: need 'clips', at most 64 clip ids")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","page":"a\u0001b"}""", "updateClip: page must be empty, for the default page, or at most 40 printable characters other than - alone")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","page":"0123456789012345678901234567890123456789x"}""", "updateClip: page must be empty, for the default page, or at most 40 printable characters other than - alone")]
    [InlineData("""{"cmd":"importClip","path":"/tmp/a.wav","page":"a\nb"}""", "importClip: page must be empty, for the default page, or at most 40 printable characters other than - alone")]
    [InlineData("""{"cmd":"renameClipPage","name":"Jokes"}""", "renameClipPage: need 'page', the page to rename, empty for the default page")]
    [InlineData("""{"cmd":"renameClipPage","page":"Memes"}""", "renameClipPage: name must be empty, for the default page, or at most 40 printable characters other than - alone")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn","page":"-"}""", "updateClip: page must be empty, for the default page, or at most 40 printable characters other than - alone")]
    [InlineData("""{"cmd":"renameClipPage","page":"Memes","name":" - "}""", "renameClipPage: name must be empty, for the default page, or at most 40 printable characters other than - alone")]
    [InlineData("""{"cmd":"exportSoundboard"}""", "exportSoundboard: need 'path', an absolute path to the archive")]
    [InlineData("""{"cmd":"exportSoundboard","path":"board.zip"}""", "exportSoundboard: need 'path', an absolute path to the archive")]
    [InlineData("""{"cmd":"exportSoundboard","path":"/home/me/.bashrc"}""", "exportSoundboard: path must end in .zip")]
    [InlineData("""{"cmd":"importSoundboard","path":"/home/me/board.zip"}""", "importSoundboard: need 'merge', a boolean: true adds the archive's clips, false replaces the library")]
    [InlineData("""{"cmd":"importSoundboard","path":"relative.zip","merge":true}""", "importSoundboard: need 'path', an absolute path to the archive")]
    public void MalformedCommandsAreRefusedWithASentence(string json, string error) => Assert.Equal(error, Check(json));

    [Fact]
    public void AnOrderOfMoreThanSixtyFourClipsIsRefused()
    {
        string clips = string.Join(",", Enumerable.Range(0, 65).Select(i => $"\"c{i}\""));
        Assert.Equal("setClipOrder: need 'clips', at most 64 clip ids", Check($$"""{"cmd":"setClipOrder","clips":[{{clips}}]}"""));
    }

    [Fact]
    public void WithoutTheHelperOnlyTurningOnAndImportingAreRefused()
    {
        Assert.Equal(Mixer.SoundboardNeedsHost, Check("""{"cmd":"setSoundboard","enabled":true}""", host: false));
        Assert.Equal(Mixer.SoundboardNeedsHost, Check("""{"cmd":"importClip","path":"/tmp/a.wav"}""", host: false));
        Assert.Equal(Mixer.SoundboardNeedsHost, Check("""{"cmd":"importSoundboard","path":"/tmp/a.zip","merge":true}""", host: false));
        Assert.Equal(Mixer.SoundboardNeedsHost, Check("""{"cmd":"restoreStarterSounds"}""", host: false));
        // An export copies files and needs no helper; pages are library edits.
        Assert.Null(Check("""{"cmd":"exportSoundboard","path":"/tmp/a.zip"}""", host: false));
        Assert.Null(Check("""{"cmd":"renameClipPage","page":"a","name":"b"}""", host: false));
        // Turning off, stopping and editing the library need nothing running.
        Assert.Null(Check("""{"cmd":"setSoundboard","enabled":false}""", host: false));
        Assert.Null(Check("""{"cmd":"stopClips"}""", host: false));
        Assert.Null(Check("""{"cmd":"stopAudition"}""", host: false));
        Assert.Null(Check("""{"cmd":"deleteClip","clip":"airhorn"}""", host: false));
        Assert.Null(Check("""{"cmd":"updateClip","clip":"airhorn","gain":0.5}""", host: false));
    }

    [Fact]
    public void TheCommandCarriesTheSoundboardFields()
    {
        Command command = JsonSerializer.Deserialize<Command>(
            """{"cmd":"updateClip","clip":"airhorn","clips":["a"],"mode":"hold","loop":false,"colour":"#000000","gain":0.25,"page":"Intro","merge":true}""", Wire)!;
        Assert.Equal("airhorn", command.Clip);
        Assert.Equal(["a"], command.Clips);
        Assert.Equal("hold", command.Mode);
        Assert.False(command.Loop);
        Assert.Equal("#000000", command.Colour);
        Assert.Equal(.25, command.Gain);
        Assert.Equal("Intro", command.Page);
        Assert.True(command.Merge);
    }

    [Fact]
    public void TheStateAndTheRepliesNameTheirFieldsAsDocumented()
    {
        var state = new SoundboardState(null, false, true, Mixer.SoundboardChannelDeleted, ["wav", "mp3"],
            [new SoundboardClipState("airhorn", "Airhorn", 2.4, .8, "overlap", false, "", null, true, null, true, SoundboardClipStatus.Ready, "Intro")], ["airhorn"])
        {
            Pages = ["Intro"],
        };
        JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(new SoundboardMessage(state), Wire))!;
        Assert.Equal("soundboard", json["type"]!.GetValue<string>());
        JsonObject board = json["soundboard"]!.AsObject();
        Assert.Equal(["channel", "enabled", "available", "error", "formats", "clips", "playing", "auditioning", "pages"], board.Select(p => p.Key));
        Assert.Equal(["Intro"], board["pages"]!.AsArray().Select(p => p!.GetValue<string>()));
        // Null is said, not left out: a client tells a deleted SFX by it,
        // and a pad that is not auditioned by the other.
        Assert.Null(board["channel"]);
        Assert.True(board.ContainsKey("channel"));
        Assert.Null(board["auditioning"]);
        Assert.True(board.ContainsKey("auditioning"));
        Assert.Equal("airhorn", JsonNode.Parse(JsonSerializer.Serialize(state with { Auditioning = "airhorn" }, Wire))!["auditioning"]!.GetValue<string>());
        JsonObject clip = board["clips"]![0]!.AsObject();
        Assert.Equal(["id", "name", "seconds", "gain", "mode", "loop", "icon", "colour", "loaded", "error", "playing", "status", "page"], clip.Select(p => p.Key));
        Assert.Equal("Intro", clip["page"]!.GetValue<string>());
        Assert.Equal("ready", clip["status"]!.GetValue<string>());
        Assert.True(clip.ContainsKey("colour"));

        JsonNode reply = JsonNode.Parse(JsonSerializer.Serialize(new ClipImportMessage(true, "Airhorn was added to the soundboard.", "airhorn"), Wire))!;
        Assert.Equal("clipImport", reply["type"]!.GetValue<string>());
        Assert.Equal("airhorn", reply["clip"]!.GetValue<string>());
        Assert.Equal("The file is empty.", WebSocketHub.OperationError(new ClipImportMessage(false, "The file is empty.", null)));
        Assert.Null(WebSocketHub.OperationError(new ClipImportMessage(true, "Fine.", "a")));

        JsonNode export = JsonNode.Parse(JsonSerializer.Serialize(new SoundboardExportMessage(true, "2 clips were exported to board.zip.", 2, []), Wire))!;
        Assert.Equal(["ok", "message", "clips", "skipped", "type"], export.AsObject().Select(p => p.Key));
        Assert.Equal("soundboardExport", export["type"]!.GetValue<string>());
        JsonNode import = JsonNode.Parse(JsonSerializer.Serialize(new SoundboardImportMessage(true, "Fine.", ["a-2"],
            new Dictionary<string, string> { ["a"] = "a-2" }, ["b: the file is empty"], false), Wire))!;
        Assert.Equal(["ok", "message", "clips", "renamed", "skipped", "replaced", "type"], import.AsObject().Select(p => p.Key));
        Assert.Equal("soundboardImport", import["type"]!.GetValue<string>());
        Assert.Equal("a-2", import["renamed"]!["a"]!.GetValue<string>());
        Assert.Equal("Too big.", WebSocketHub.OperationError(new SoundboardExportMessage(false, "Too big.", 0, [])));
        Assert.Equal("Not a zip.", WebSocketHub.OperationError(new SoundboardImportMessage(false, "Not a zip.", [], new Dictionary<string, string>(), [], false)));
        JsonNode starter = JsonNode.Parse(JsonSerializer.Serialize(new StarterSoundsMessage(true, "Every starter sound is on the soundboard already.", [], []), Wire))!;
        Assert.Equal(["ok", "message", "clips", "skipped", "type"], starter.AsObject().Select(p => p.Key));
        Assert.Equal("starterSounds", starter["type"]!.GetValue<string>());
        // Nothing to add back is not a failure; a set that is not installed is.
        Assert.Null(WebSocketHub.OperationError(new StarterSoundsMessage(true, "Every starter sound is on the soundboard already.", [], [])));
        Assert.Equal("Not installed.", WebSocketHub.OperationError(new StarterSoundsMessage(false, "Not installed.", [], [])));

        // The state carries the board under mixer.soundboard.
        var mixer = new MixerState { Mixes = [], Channels = [], Soundboard = state };
        Assert.NotNull(JsonNode.Parse(JsonSerializer.Serialize(mixer, Wire))!["soundboard"]!["clips"]);
    }

    [Theory]
    [InlineData("clip 3 17 ok 96000 2", 3, 17u, "Loaded", 96000, 2, null)]
    [InlineData("clip 63 4294967295 ok 1 1", 63, 4294967295u, "Loaded", 1, 1, null)]
    [InlineData("clip 0 9 error the clip is 31.0 s long; the limit is 30 s", 0, 9u, "Failed", 0, 0, "the clip is 31.0 s long; the limit is 30 s")]
    [InlineData("clip 5 unloaded", 5, 0u, "Unloaded", 0, 0, null)]
    public void ClipLinesAreRead(string line, int slot, uint token, string outcome, long frames, int channels, string? message)
        => Assert.Equal(new ClipReply(slot, Enum.Parse<ClipOutcome>(outcome), frames, channels, message) { Token = token }, NativePluginHost.ParseClip(line));

    [Theory]
    [InlineData("clip 64 1 ok 1 2")]
    [InlineData("clip -1 1 ok 1 2")]
    [InlineData("clip 3 1 ok 1 3")]
    [InlineData("clip 3 1 ok 1")]
    [InlineData("clip 3 1 ok x 2")]
    [InlineData("clip 3 1 error")]
    [InlineData("clip 3 ok 1 2")]
    [InlineData("clip 3 x ok 1 2")]
    [InlineData("clip 3 4294967296 ok 1 2")]
    [InlineData("clip 3 error the old form")]
    [InlineData("clip 3 unloaded now")]
    [InlineData("clip x 1 ok 1 2")]
    [InlineData("clips 3 1 ok 1 2")]
    public void MalformedClipLinesAreIgnored(string line) => Assert.Null(NativePluginHost.ParseClip(line));

    [Fact]
    public void ANoteTheHelperWritesIsNeverItsLastWords()
    {
        var line = new System.Text.StringBuilder();
        Assert.Equal("unsupported audio quantum or sample-rate change; restart the chain",
            NativePluginHost.FoldErrorBlock(line, "unsupported audio quantum or sample-rate change; restart the chain\nnote: slot 3 holds no clip\nnote: the player is busy; a press was dropped\n"));
        Assert.Null(NativePluginHost.FoldErrorBlock(line, "note: invalid player command: x\n"));
        Assert.True(Mixer.DecoderCrash(11) && Mixer.DecoderCrash(6) && !Mixer.DecoderCrash(9) && !Mixer.DecoderCrash(15));
    }

    [Fact]
    public void ProbeLinesAreRead()
    {
        Assert.Equal(new ClipProbe("mp3", 2, 44100, 88200), PipeWireAdapter.ParseProbe("probe ok mp3 2 44100 88200\n"));
        Assert.Equal(2.0, PipeWireAdapter.ParseProbe("probe ok wav 1 48000 96000")!.Seconds);
        Assert.Equal("audio", PipeWireAdapter.ParseProbe("probe ok other 1 8000 8000")!.Extension);
        Assert.Equal("The clip is 31.0 s long; the limit is 30 s.",
            Assert.Throws<InvalidOperationException>(() => PipeWireAdapter.ParseProbe("probe error the clip is 31.0 s long; the limit is 30 s")).Message);
        Assert.Null(PipeWireAdapter.ParseProbe(""));
        Assert.Null(PipeWireAdapter.ParseProbe("probe ok wav 3 48000 96000"));
        Assert.Null(PipeWireAdapter.ParseProbe("probe ok exe 2 48000 96000"));
        Assert.Null(PipeWireAdapter.ParseProbe("probe ok wav 2 0 96000"));
    }

    [Fact]
    public void TheGraphRateIsReadFromTheSettingsInTheSnapshot()
    {
        static JsonElement[] Objects(params string[] items)
            => [.. JsonDocument.Parse("[" + string.Join(",", items) + "]").RootElement.EnumerateArray().Select(e => e.Clone())];
        static string Settings(string entries)
            => """{"id":31,"type":"PipeWire:Interface:Metadata","props":{"metadata.name":"settings"},"metadata":[""" + entries + "]}";
        const string other = """{"id":5,"type":"PipeWire:Interface:Metadata","props":{"metadata.name":"default"},"metadata":[{"subject":0,"key":"clock.rate","value":96000}]}""";
        Assert.Equal(48000, PipeWireAdapter.ParseGraphSampleRate(Objects(other,
            Settings("""{"subject":0,"key":"clock.rate","type":"","value":48000},{"subject":0,"key":"clock.force-rate","type":"","value":0}"""))));
        Assert.Equal(44100, PipeWireAdapter.ParseGraphSampleRate(Objects(
            Settings("""{"subject":0,"key":"clock.rate","value":48000},{"subject":0,"key":"clock.force-rate","value":"44100"}"""))));
        // Another subject's entry is not the graph's.
        Assert.Equal(48000, PipeWireAdapter.ParseGraphSampleRate(Objects(
            Settings("""{"subject":7,"key":"clock.force-rate","value":96000},{"subject":0,"key":"clock.rate","value":48000}"""))));
        Assert.Null(PipeWireAdapter.ParseGraphSampleRate(Objects(other)));
        Assert.Null(PipeWireAdapter.ParseGraphSampleRate(Objects(Settings("""{"subject":0,"key":"clock.rate","value":1}"""))));
    }

    [Fact]
    public void FormatsAreTheOnesTheBoardKnows()
    {
        Assert.Equal(["wav", "flac", "mp3"], PipeWireAdapter.ParsePlayerFormats("""{"formats":["mp3","flac","wav","exe"]}"""));
        Assert.Empty(PipeWireAdapter.ParsePlayerFormats("not json"));
        Assert.Equal(["player"], NativePluginHost.Arguments(new InsertDefinition { Id = "soundboard", Kind = "player", Plugin = "player" }, null));
    }
}
