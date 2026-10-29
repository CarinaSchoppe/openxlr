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
    [InlineData("""{"cmd":"setClipOrder","clips":["b","a"]}""")]
    [InlineData("""{"cmd":"setClipOrder","clips":[]}""")]
    [InlineData("""{"cmd":"getSoundboard"}""")]
    public void WellFormedCommandsPass(string json) => Assert.Null(Check(json));

    [Theory]
    [InlineData("""{"cmd":"setSoundboard"}""", "setSoundboard: need 'enabled', a boolean")]
    [InlineData("""{"cmd":"importClip"}""", "importClip: need 'path', an absolute path to the file")]
    [InlineData("""{"cmd":"importClip","path":"airhorn.mp3"}""", "importClip: need 'path', an absolute path to the file")]
    [InlineData("""{"cmd":"importClip","path":"/tmp/a\nb.mp3"}""", "importClip: need 'path', an absolute path to the file")]
    [InlineData("""{"cmd":"importClip","path":"/tmp/a.mp3","name":""}""", "importClip: name must contain 1 to 60 printable characters")]
    [InlineData("""{"cmd":"updateClip","clip":"airhorn"}""", "updateClip: give at least one of name, gain, mode, loop, icon and colour")]
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
    [InlineData("""{"cmd":"setClipOrder"}""", "setClipOrder: need 'clips', at most 64 clip ids")]
    [InlineData("""{"cmd":"setClipOrder","clips":["a","B"]}""", "setClipOrder: need 'clips', at most 64 clip ids")]
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
        // Turning off, stopping and editing the library need nothing running.
        Assert.Null(Check("""{"cmd":"setSoundboard","enabled":false}""", host: false));
        Assert.Null(Check("""{"cmd":"stopClips"}""", host: false));
        Assert.Null(Check("""{"cmd":"deleteClip","clip":"airhorn"}""", host: false));
        Assert.Null(Check("""{"cmd":"updateClip","clip":"airhorn","gain":0.5}""", host: false));
    }

    [Fact]
    public void TheCommandCarriesTheSoundboardFields()
    {
        Command command = JsonSerializer.Deserialize<Command>(
            """{"cmd":"updateClip","clip":"airhorn","clips":["a"],"mode":"hold","loop":false,"colour":"#000000","gain":0.25}""", Wire)!;
        Assert.Equal("airhorn", command.Clip);
        Assert.Equal(["a"], command.Clips);
        Assert.Equal("hold", command.Mode);
        Assert.False(command.Loop);
        Assert.Equal("#000000", command.Colour);
        Assert.Equal(.25, command.Gain);
    }

    [Fact]
    public void TheStateAndTheRepliesNameTheirFieldsAsDocumented()
    {
        var state = new SoundboardState(null, false, true, Mixer.SoundboardChannelDeleted, ["wav", "mp3"],
            [new SoundboardClipState("airhorn", "Airhorn", 2.4, .8, "overlap", false, "", null, true, null, true, SoundboardClipStatus.Ready)], ["airhorn"]);
        JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(new SoundboardMessage(state), Wire))!;
        Assert.Equal("soundboard", json["type"]!.GetValue<string>());
        JsonObject board = json["soundboard"]!.AsObject();
        Assert.Equal(["channel", "enabled", "available", "error", "formats", "clips", "playing"], board.Select(p => p.Key));
        // Null is said, not left out: a client tells a deleted SFX by it.
        Assert.Null(board["channel"]);
        Assert.True(board.ContainsKey("channel"));
        JsonObject clip = board["clips"]![0]!.AsObject();
        Assert.Equal(["id", "name", "seconds", "gain", "mode", "loop", "icon", "colour", "loaded", "error", "playing", "status"], clip.Select(p => p.Key));
        Assert.Equal("ready", clip["status"]!.GetValue<string>());
        Assert.True(clip.ContainsKey("colour"));

        JsonNode reply = JsonNode.Parse(JsonSerializer.Serialize(new ClipImportMessage(true, "Airhorn was added to the soundboard.", "airhorn"), Wire))!;
        Assert.Equal("clipImport", reply["type"]!.GetValue<string>());
        Assert.Equal("airhorn", reply["clip"]!.GetValue<string>());
        Assert.Equal("The file is empty.", WebSocketHub.OperationError(new ClipImportMessage(false, "The file is empty.", null)));
        Assert.Null(WebSocketHub.OperationError(new ClipImportMessage(true, "Fine.", "a")));

        // The state carries the board under mixer.soundboard.
        var mixer = new MixerState { Mixes = [], Channels = [], Soundboard = state };
        Assert.NotNull(JsonNode.Parse(JsonSerializer.Serialize(mixer, Wire))!["soundboard"]!["clips"]);
    }

    [Theory]
    [InlineData("clip 3 ok 96000 2", 3, "Loaded", 96000, 2, null)]
    [InlineData("clip 63 ok 1 1", 63, "Loaded", 1, 1, null)]
    [InlineData("clip 0 error the clip is 31.0 s long; the limit is 30 s", 0, "Failed", 0, 0, "the clip is 31.0 s long; the limit is 30 s")]
    [InlineData("clip 5 unloaded", 5, "Unloaded", 0, 0, null)]
    public void ClipLinesAreRead(string line, int slot, string outcome, long frames, int channels, string? message)
        => Assert.Equal(new ClipReply(slot, Enum.Parse<ClipOutcome>(outcome), frames, channels, message), NativePluginHost.ParseClip(line));

    [Theory]
    [InlineData("clip 64 ok 1 2")]
    [InlineData("clip -1 ok 1 2")]
    [InlineData("clip 3 ok 1 3")]
    [InlineData("clip 3 ok 1")]
    [InlineData("clip 3 ok x 2")]
    [InlineData("clip 3 error")]
    [InlineData("clip 3 unloaded now")]
    [InlineData("clip x ok 1 2")]
    [InlineData("clips 3 ok 1 2")]
    public void MalformedClipLinesAreIgnored(string line) => Assert.Null(NativePluginHost.ParseClip(line));

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
