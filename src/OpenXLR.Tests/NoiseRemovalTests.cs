using System.Reflection;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Microphone noise removal: its model, validation, how it is saved and
/// recalled, the engines the native helper reports, the stage it asks for,
/// and the state clients read. The audio itself is checked by
/// <c>make -C native test-denoise</c> and, on a private PipeWire server, by
/// the DSP integration tests.
/// </summary>
public sealed class NoiseRemovalTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system" or "guest";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public int OverrideCount => 0;
    }

    private static string? Check(string json, bool host = true)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, host);

    /// <summary>A mixer whose stage helper is a script that reports <paramref name="engines"/>.</summary>
    private static Mixer WithHelper(string directory, string engines)
    {
        var mixer = new Mixer();
        string helper = Path.Combine(directory, "openxlr-lv2-host");
        ExecutableScript.Write(helper, $$"""
            if [ "$1" = engines ]; then echo '{"denoise":[{{engines}}],"rate":48000}'; exit 0; fi
            exit 1
            """);
        ((PipeWireAdapter)typeof(Mixer).GetField("_pw", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!).EqHost = helper;
        return mixer;
    }

    private static MicNoiseState Noise(Mixer mixer, string channel = "xlr1")
        => mixer.Snapshot().Channels.Single(c => c.Id == channel).Noise!;

    [Fact]
    public void TheDefaultIsOffWithRnNoiseUnlimited()
    {
        MicNoise noise = MicNoise.Default;
        Assert.False(noise.Enabled);
        Assert.Equal("rnnoise", noise.Engine);
        Assert.Equal(100, noise.Strength);
        Assert.Equal(["rnnoise", "deepfilternet"], MicNoise.Engines);
        Assert.Equal(0, MicNoise.EngineIndex("rnnoise"));
        Assert.Equal(1, MicNoise.EngineIndex("deepfilternet"));
        Assert.Equal(-1, MicNoise.EngineIndex("voice"));
        Assert.Equal(-1, MicNoise.EngineIndex(null));
    }

    [Theory]
    [InlineData("rnnoise", 0, null)]
    [InlineData("deepfilternet", 100, null)]
    [InlineData("rnnoise", 42.4, null)]
    [InlineData("rnn", 30, "engine must be one of rnnoise, deepfilternet")]
    [InlineData("rnnoise", -0.5, "strength must be 0 to 100 dB")]
    [InlineData("rnnoise", 100.5, "strength must be 0 to 100 dB")]
    [InlineData("rnnoise", double.NaN, "strength must be 0 to 100 dB")]
    public void ASettingOutsideTheRangeIsRefusedNotClamped(string engine, double strength, string? error)
    {
        using var culture = new CultureScope("it-IT");
        Assert.Equal(error, MicNoise.Check(new MicNoise { Enabled = true, Engine = engine, Strength = strength }));
    }

    [Fact]
    public void StrengthSnapsToWholeDecibels()
    {
        Assert.Equal(42, MicNoise.SnapStrength(42.4));
        Assert.Equal(43, MicNoise.SnapStrength(42.5));
        MicNoise? read = MicNoise.Read(JsonDocument.Parse("""{"enabled":true,"engine":"deepfilternet","strength":17.6,"active":"x"}""").RootElement, out string? error);
        Assert.Null(error);
        Assert.Equal(new MicNoise { Enabled = true, Engine = "deepfilternet", Strength = 18 }, read);
        Assert.Null(MicNoise.Read(JsonDocument.Parse("""{"enabled":true,"engine":"deepfilternet"}""").RootElement, out error));
        Assert.Equal("need 'enabled', 'engine' and 'strength'", error);
        Assert.Null(MicNoise.Read(JsonDocument.Parse("""{"enabled":true,"engine":"x","strength":1}""").RootElement, out error));
        Assert.StartsWith("engine must be one of", error);
    }

    [Theory]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"xlr1","enabled":true}""", null)]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"xlr2","engine":"deepfilternet"}""", null)]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"guest","strength":24}""", null)]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"xlr1","enabled":false,"engine":"rnnoise","strength":0}""", null)]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"xlr1"}""", "setNoiseRemoval: need at least one of 'enabled', 'engine' and 'strength'")]
    [InlineData("""{"cmd":"setNoiseRemoval","enabled":true}""", "setNoiseRemoval: need 'channel'")]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"aux","enabled":true}""", "setNoiseRemoval: 'aux' is not a microphone input")]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"system","enabled":true}""", "setNoiseRemoval: 'system' is not a microphone input")]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"xlr1","engine":"krisp"}""", "setNoiseRemoval: engine must be one of rnnoise, deepfilternet")]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"xlr1","strength":101}""", "setNoiseRemoval: strength must be 0 to 100 dB")]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"xlr1","strength":-1}""", "setNoiseRemoval: strength must be 0 to 100 dB")]
    public void TheCommandIsCheckedBeforeTheMixer(string json, string? error) => Assert.Equal(error, Check(json));

    [Fact]
    public void SwitchingItOnNeedsTheNativeHelper()
    {
        Assert.Equal(Mixer.NoiseNeedsHost, Check("""{"cmd":"setNoiseRemoval","channel":"xlr1","enabled":true}""", host: false));
        // Off, the engine and the strength are only stored.
        Assert.Null(Check("""{"cmd":"setNoiseRemoval","channel":"xlr1","enabled":false}""", host: false));
        Assert.Null(Check("""{"cmd":"setNoiseRemoval","channel":"xlr1","strength":20}""", host: false));
    }

    [Fact]
    public void TheEngineListIsReadFromTheHelpersOwnReport()
    {
        Assert.Equal(["rnnoise", "deepfilternet"], PipeWireAdapter.ParseNoiseEngines("""{"denoise":["deepfilternet","rnnoise"],"rate":48000}"""));
        Assert.Equal(["rnnoise"], PipeWireAdapter.ParseNoiseEngines("""{"denoise":["rnnoise","future"],"rate":48000}"""));
        Assert.Empty(PipeWireAdapter.ParseNoiseEngines("usage: openxlr-lv2-host lv2 URI"));
        Assert.Empty(PipeWireAdapter.ParseNoiseEngines(""));
        Assert.Empty(PipeWireAdapter.ParseNoiseEngines("""{"denoise":"rnnoise"}"""));
        Assert.Empty(PipeWireAdapter.ParseNoiseEngines(null));
    }

    [Fact]
    public void TheMixerStoresSnappedValuesAndRefusesAnEngineTheHelperLacks()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-noise-").FullName;
        try
        {
            using Mixer mixer = WithHelper(dir, "\"rnnoise\"");
            Assert.Equal(MicNoise.Default, mixer.NoiseFor("xlr1"));
            Assert.Equal(["rnnoise"], mixer.NoiseEngines);
            mixer.SetNoiseRemoval("xlr1", new(Enabled: true, Strength: 24.6));
            Assert.Equal(new MicNoise { Enabled = true, Engine = "rnnoise", Strength = 25 }, mixer.NoiseFor("xlr1"));
            var missing = Assert.Throws<ArgumentException>(() => mixer.SetNoiseRemoval("xlr1", new(Engine: "deepfilternet")));
            Assert.Equal(Mixer.NoiseEngineMissing("deepfilternet"), missing.Message);
            Assert.Contains("built without DeepFilterNet", missing.Message);
            Assert.Throws<ArgumentException>(() => mixer.SetNoiseRemoval("xlr1", new(Strength: 101)));
            Assert.Throws<ArgumentException>(() => mixer.SetNoiseRemoval("xlr1", new()));
            Assert.Throws<ArgumentException>(() => mixer.SetNoiseRemoval("aux", new(Enabled: true)));
            Assert.Equal(25, mixer.NoiseFor("xlr1").Strength);
            Assert.Equal(MicNoise.Default, mixer.NoiseFor("xlr2"));

            Assert.True(mixer.ExportSettings().NoiseRemoval["xlr1"].Enabled);
            Assert.False(mixer.ExportSettings().NoiseRemoval.ContainsKey("xlr2"), "A setting never made is not written: absent means off.");
            MixerScene scene = mixer.ExportScene();
            Assert.Equal(["xlr1", "xlr2"], scene.NoiseRemoval!.Keys.Order());
            Assert.Equal(MicNoise.Default, scene.NoiseRemoval["xlr2"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AHelperWithDeepFilterNetTakesIt()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-noise-").FullName;
        try
        {
            using Mixer mixer = WithHelper(dir, "\"rnnoise\",\"deepfilternet\"");
            mixer.SetNoiseRemoval("xlr2", new(Engine: "deepfilternet", Enabled: true, Strength: 40));
            Assert.Equal(new MicNoise { Enabled = true, Engine = "deepfilternet", Strength = 40 }, mixer.NoiseFor("xlr2"));
            MicNoiseState state = Noise(mixer, "xlr2");
            Assert.Equal(["rnnoise", "deepfilternet"], state.Engines);
            Assert.True(state.Available);
            Assert.Null(state.Active);
            Assert.Null(state.LatencyMs);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheStateSaysWhyNoiseRemovalCannotRun()
    {
        using var mixer = new Mixer();
        ((PipeWireAdapter)typeof(Mixer).GetField("_pw", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!).EqHost
            = "/nonexistent/openxlr-lv2-host";
        mixer.SetNoiseRemoval("xlr1", new(Enabled: true));
        MicNoiseState state = Noise(mixer);
        Assert.True(state.Enabled);
        Assert.False(state.Available);
        Assert.Empty(state.Engines);
        Assert.Equal(Mixer.NoiseNeedsHost, state.Error);
        Assert.Null(Noise(mixer, "xlr2").Error);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "aux").Noise);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "system").Noise);

        using JsonDocument json = JsonSerializer.SerializeToDocument(mixer.Snapshot(), Web);
        JsonElement xlr1 = json.RootElement.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "xlr1");
        JsonElement noise = xlr1.GetProperty("noise");
        Assert.True(noise.GetProperty("enabled").GetBoolean());
        Assert.Equal("rnnoise", noise.GetProperty("engine").GetString());
        Assert.Equal(100, noise.GetProperty("strength").GetDouble());
        Assert.Equal(JsonValueKind.Array, noise.GetProperty("engines").ValueKind);
        Assert.Equal(JsonValueKind.Null, noise.GetProperty("active").ValueKind);
        Assert.Equal(JsonValueKind.Null, noise.GetProperty("latencyMs").ValueKind);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Noise removal runs at 48 kHz, and the audio graph runs at 44100 Hz. It passes the microphone through untouched.")]
    [InlineData(2, "This installation's plugin host was built without DeepFilterNet.")]
    [InlineData(3, "DeepFilterNet could not start. Switch it off and on to try again, or choose the other engine.")]
    [InlineData(4, "DeepFilterNet stopped or could not keep up on this computer and is off. Switch it off and on to try again, or choose the other engine.")]
    public void TheStagesStatusBecomesASentence(int status, string? sentence)
        => Assert.Equal(sentence, Mixer.NoiseStatusText("deepfilternet", status, 44100));

    [Fact]
    public void NoiseRemovalAloneAsksForTheMicrophoneStage()
    {
        using var mixer = new Mixer();
        Assert.False(mixer.WantsMicrophoneStage("xlr1"));
        mixer.SetNoiseRemoval("xlr1", new(Enabled: true));
        Assert.True(mixer.WantsMicrophoneStage("xlr1"));
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"), "The EQ itself stays off.");
        Assert.False(mixer.WantsMicrophoneStage("xlr2"));
        mixer.SetNoiseRemoval("xlr1", new(Enabled: false));
        Assert.False(mixer.WantsMicrophoneStage("xlr1"));
        mixer.SetEqEnabled("xlr1", true);
        Assert.True(mixer.WantsMicrophoneStage("xlr1"));
        Assert.False(mixer.WantsMicrophoneStage("aux"));
    }

    [Fact]
    public void ARecallChangesOnlyTheSettingsItNames()
    {
        MicNoise on = new() { Enabled = true, Engine = "deepfilternet", Strength = 30 };
        MicNoise guest = new() { Enabled = false, Strength = 12 };
        var current = new Dictionary<string, MicNoise> { ["xlr1"] = on, ["guest"] = guest };
        // A profile from before noise removal carries none: nothing changes.
        Assert.Equal(current, Mixer.RecallNoise(current, null));
        Assert.Equal(current, Mixer.RecallNoise(current, new Dictionary<string, MicNoise>()));
        var next = Mixer.RecallNoise(current, new Dictionary<string, MicNoise> { ["xlr2"] = on with { Strength = 12.4 } });
        Assert.Same(on, next["xlr1"]);
        Assert.Same(guest, next["guest"]);
        Assert.Equal(12, next["xlr2"].Strength);
        // An unreadable entry never replaces a good one.
        next = Mixer.RecallNoise(current, new Dictionary<string, MicNoise> { ["xlr1"] = on with { Engine = "x" } });
        Assert.Same(on, next["xlr1"]);
    }

    [Fact]
    public void TheSettingsFileKeepsEveryReadableSettingAndDropsTheRest()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-noise-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """
                {
                  "noiseRemoval": {
                    "xlr1": {"enabled": true, "engine": "deepfilternet", "strength": 30.4},
                    "cap1": {"enabled": false, "engine": "rnnoise", "strength": 100},
                    "xlr2": {"enabled": true, "engine": "rnnoise", "strength": 140},
                    "guest": {"enabled": true, "engine": "krisp", "strength": 20}
                  }
                }
                """);
            MixerSettings? settings = MixerSettings.Load(path, out string? warning);
            Assert.NotNull(settings);
            Assert.Contains("noiseRemoval.xlr2: strength must be 0 to 100 dB", warning);
            Assert.Contains("noiseRemoval.guest: engine must be one of", warning);
            Assert.Equal(["cap1", "xlr1"], settings!.NoiseRemoval.Keys.Order());
            Assert.Equal(30, settings.NoiseRemoval["xlr1"].Strength);
            Assert.Null(settings.Save(path));
            MixerSettings again = MixerSettings.Load(path, out string? clean)!;
            Assert.Null(clean);
            Assert.Equal(settings.NoiseRemoval, again.NoiseRemoval);
            Assert.Contains("\"engine\": \"deepfilternet\"", File.ReadAllText(path));
            // A file from before noise removal has it off everywhere.
            File.WriteAllText(path, "{}");
            Assert.Empty(MixerSettings.Load(path, out _)!.NoiseRemoval);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProfileRoundTripsItsSettingAndOneWithoutLeavesItOut()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        MicNoise noise = new() { Enabled = true, Engine = "deepfilternet", Strength = 18 };
        var scene = new MixerScene { NoiseRemoval = new() { ["xlr1"] = noise } };
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(JsonSerializer.Serialize(scene, json), json)!;
        Assert.Equal(noise, back.NoiseRemoval!["xlr1"]);
        SavedMixerValidation.Validate(back);
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("{}", json)!.NoiseRemoval);
        MixerScene bad = back with { NoiseRemoval = new() { ["xlr1"] = noise with { Strength = -3 } } };
        Assert.Contains("noiseRemoval.xlr1", Assert.Throws<JsonException>(() => SavedMixerValidation.Validate(bad)).Message);
    }

    [Fact]
    public void TheNativeControlsCarryEveryValueByItsSymbol()
    {
        var noise = new MicNoise { Enabled = true, Engine = "deepfilternet", Strength = 35 };
        Dictionary<string, double> controls = SoftwareEqBackend.Controls(noise);
        Assert.Equal(3, controls.Count);
        Assert.Equal(1, controls["denoise_enabled"]);
        Assert.Equal(1, controls["denoise_engine"]);
        Assert.Equal(35, controls["denoise_strength"]);
        Assert.Equal(0, SoftwareEqBackend.Controls(MicNoise.Default)["denoise_engine"]);
        // The stage starts with the EQ's controls and these together.
        Dictionary<string, double> all = SoftwareEqBackend.Controls(MicEq.Default, noise);
        Assert.Equal(21 + 3, all.Count);
        // The C side declares the same symbols and meters.
        string source = File.ReadAllText(Path.Combine(MicEqTests.RepoRoot, "native", "denoise.c"));
        foreach (string symbol in controls.Keys.Concat(SoftwareEqBackend.Meters.Where(m => m.StartsWith("denoise_", StringComparison.Ordinal))))
            Assert.Contains($"\"{symbol}\"", source);
        Assert.Contains("\"denoise_strength\", 0, 100, 100", source);
    }
}
