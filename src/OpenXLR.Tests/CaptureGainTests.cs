using System.Reflection;
using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A capture input's gain: its source's PipeWire volume, read as the device's
/// own gain where the device has a hardware capture control. The numbers are
/// the desk's Logitech BRIO, read with pactl and amixer: a base volume of -54
/// dB and a 'Mic Capture Volume' of +18 to +54 dB in 0.5 dB steps. A source
/// volume of -18 dB put the control at value 36, +36 dB; -42 dB put it at its
/// floor, +18 dB, with the remaining 6 dB taken off in software.
///
/// The mixer tests put fake helpers first on PATH, which the process shares,
/// so they run with the other tests that change the environment.
/// </summary>
[Collection("xdg-config")]
public sealed class CaptureGainTests
{
    private const string Brio = "alsa_input.usb-046d_Logi_4K_Stream_Edition_C971DF30-03.analog-stereo";
    private const string Onboard = "alsa_input.pci-0000_7d_00.6.analog-stereo";
    private const string ProRaw = "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_AAY3H5481241GS-00.pro-input-0";
    private const string Dock = "alsa_input.usb-Elgato_Elgato_Wave_XLR_Dock_MK.2_AB2JA536121KPI-00.analog-stereo";

    private static string Source(string name, long volume, long baseVolume, bool hardware, string card)
    {
        string flags = hardware ? "\"HW_MUTE_CTRL\",\"HW_VOLUME_CTRL\"," : "";
        string Level(long value) => "{\"value\":" + value + ",\"value_percent\":\"x\",\"db\":\"x\"}";
        return "{\"index\":1,\"state\":\"RUNNING\",\"name\":\"" + name + "\",\"driver\":\"PipeWire\",\"mute\":false,"
            + "\"volume\":{\"front-left\":" + Level(volume) + ",\"front-right\":" + Level(volume - 100) + "},"
            + "\"balance\":0.0,\"base_volume\":" + Level(baseVolume) + ","
            + "\"flags\":[\"HARDWARE\"," + flags + "\"DECIBEL_VOLUME\",\"LATENCY\"],"
            + "\"properties\":{\"alsa.card\":\"" + card + "\",\"node.name\":\"" + name + "\"}}";
    }

    private static string Sources(long brio = 65536) => "[" + string.Join(",",
        Source(Brio, brio, 8250, true, "3"), Source(Onboard, 32846, 65536, false, "2"),
        Source(ProRaw, 48316, 65536, false, "6"), Source(Dock, 65536, 65536, false, "1")) + "]";

    private const string BrioControls = """
        numid=2,iface=MIXER,name='Mic Capture Switch'
          ; type=BOOLEAN,access=rw------,values=1
          : values=on
        numid=3,iface=MIXER,name='Mic Capture Volume'
          ; type=INTEGER,access=rw---R--,values=1,min=0,max=72,step=0
          : values=72
          | dBminmax-min=18.00dB,max=54.00dB
        numid=1,iface=PCM,name='Capture Channel Map'
          ; type=INTEGER,access=r--v-R--,values=2,min=0,max=36,step=0
          : values=3,4
          | container
            | chmap-fixed=FL,FR
        """;

    private static IReadOnlyDictionary<string, SourceVolume> Parse(string json, params string[] names)
        => CaptureGain.ParseSources(Encoding.UTF8.GetBytes(json), new HashSet<string>(names));

    [Fact]
    public void TheBriosGainReadsAsItsAlsaControlDoes()
    {
        SourceVolume brio = Parse(Sources(), Brio)[Brio];
        Assert.Equal(new SourceVolume(Brio, 65536, 8250, true, "3"), brio);
        Assert.True(CaptureGain.IsHardware(brio));
        Assert.Equal(-54, CaptureGain.VolumeDb(8250), 2);
        Assert.Equal(18, CaptureGain.HardwareMinimum(BrioControls, 54));

        // pactl at 100% is the control at its top, +54 dB.
        CaptureGainStatus top = CaptureGain.Status(brio, 18);
        Assert.Equal(new CaptureGainStatus(54, 18, 54, true), top);
        // -18 dB on the source is +36 dB on the control, the value pactl took on the desk.
        Assert.Equal(32846, CaptureGain.RawFor(brio, top, 36));
        Assert.Equal(36, CaptureGain.Status(brio with { Volume = 32846 }, 18).GainDb, 1);
        // The range holds: nothing past the device's top, nothing under its floor.
        Assert.Equal(65536, CaptureGain.RawFor(brio, top, 70));
        Assert.Equal(CaptureGain.RawVolume(-36), CaptureGain.RawFor(brio, top, -5));
        // A level another program set under the floor still reads as what it is.
        Assert.Equal(12, CaptureGain.Status(brio with { Volume = 13076 }, 18).GainDb, 1);
        // Without the ALSA control's floor, the range reaches 60 dB down from the top.
        Assert.Equal(new CaptureGainStatus(54, -6, 54, true), CaptureGain.Status(brio, null));
    }

    [Fact]
    public void ASourceWithoutAHardwareControlIsASoftwareLevel()
    {
        SourceVolume onboard = Parse(Sources(), Onboard)[Onboard];
        Assert.False(CaptureGain.IsHardware(onboard));
        CaptureGainStatus status = CaptureGain.Status(onboard, null);
        Assert.Equal(new CaptureGainStatus(-18, CaptureGain.SoftwareMinDb, CaptureGain.SoftwareMaxDb, false), status);
        Assert.Equal(65536, CaptureGain.RawFor(onboard, status, 0));
        Assert.Equal(CaptureGain.RawVolume(10), CaptureGain.RawFor(onboard, status, 30));
        // A hardware flag with a base volume at 0 dB has no gain above unity to report.
        Assert.False(CaptureGain.IsHardware(onboard with { HardwareVolume = true }));
        // Silence reads at the bottom, not as negative infinity.
        Assert.True(CaptureGain.Status(onboard with { Volume = 0 }, null).GainDb < CaptureGain.SoftwareMinDb);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(-17.5)]
    [InlineData(0)]
    [InlineData(10)]
    public void ARawVolumeRoundTripsItsDecibels(double db)
        => Assert.Equal(db, CaptureGain.VolumeDb(CaptureGain.RawVolume(db)), 2);

    [Fact]
    public void TheParserKeepsOnlyNamedUniqueSources()
    {
        Assert.Empty(Parse(Sources(), "missing"));
        Assert.Equal([Brio], Parse(Sources(), Brio, "missing").Keys);
        // Two sources of one name: neither can be told to be the right one.
        Assert.Empty(Parse("[" + Source(Brio, 1, 8250, true, "3") + "," + Source(Brio, 2, 8250, true, "3") + "]", Brio));
        // The loudest channel is the source's volume, as desktop applets show it.
        Assert.Equal(65536, Parse(Sources(), Brio)[Brio].Volume);
        // No card number, no card.
        Assert.Null(Parse(Sources().Replace("\"alsa.card\":\"3\"", "\"alsa.card\":\"x\"", StringComparison.Ordinal), Brio)[Brio].AlsaCard);
        Assert.Empty(Parse("{}", Brio));
    }

    [Fact]
    public void TheAlsaFloorComesFromTheControlWhoseTopTheBaseVolumeNames()
    {
        Assert.Null(CaptureGain.HardwareMinimum(BrioControls, 30));
        Assert.Null(CaptureGain.HardwareMinimum("", 54));
        // A control given as a scale: -12 dB in 0.5 dB steps over 0..72 tops out at +24 dB.
        const string scaled = """
            numid=4,iface=MIXER,name='Capture Volume'
              ; type=INTEGER,access=rw---R--,values=2,min=0,max=72,step=0
              : values=60,60
              | dBscale-min=-12.00dB,step=0.50dB,mute=0
            """;
        Assert.Equal(-12, CaptureGain.HardwareMinimum(scaled, 24));
        // A playback volume never stands for the capture gain.
        Assert.Null(CaptureGain.HardwareMinimum(scaled.Replace("Capture Volume", "PCM Playback Volume", StringComparison.Ordinal), 24));
        // Two capture controls with the same top and different floors: no guess.
        Assert.Null(CaptureGain.HardwareMinimum(scaled + "\n" + scaled.Replace("-12.00dB", "-6.00dB", StringComparison.Ordinal)
            .Replace("step=0.50dB", "step=0.4166667dB", StringComparison.Ordinal), 24));
    }

    [Theory]
    [InlineData(ProRaw, true)]
    [InlineData(Dock, true)]
    [InlineData("alsa_input.usb-Elgato_Systems_Elgato_Wave_3_BS12345-00.mono-fallback", true)]
    [InlineData("alsa_input.usb-Elgato_Systems_Elgato_Wave_1_BS12345-00.mono-fallback", true)]
    [InlineData("OpenXLR_stream", true)]
    [InlineData(Brio, false)]
    [InlineData(Onboard, false)]
    [InlineData("alsa_input.usb-Elgato_Systems_Elgato_Facecam-00.analog-stereo", false)]
    public void WaveUnitsAndOpenXlrNodesAreNeverWritten(string source, bool isProtected)
        => Assert.Equal(isProtected, CaptureGain.IsProtectedSource(source));

    [Fact]
    public void TheMixerReportsEachCaptureInputsGainAndLeavesUnitSourcesAlone()
    {
        using var fake = new FakeAudio();
        Assert.True(fake.Mixer.SyncCaptureGains());
        Dictionary<string, ChannelStatus> channels = fake.Mixer.Snapshot().Channels.ToDictionary(c => c.Id);
        Assert.Equal(new CaptureGainStatus(54, 18, 54, true), channels["brio"].CaptureGain);
        Assert.Equal(new CaptureGainStatus(-18, -60, 10, false), channels["onboard"].CaptureGain);
        // The Pro's raw source, a dock's node and a capture input that remembers a unit report no gain of their own.
        Assert.Null(channels["pro"].CaptureGain);
        Assert.Null(channels["dock"].CaptureGain);
        Assert.Null(channels["unit"].CaptureGain);
        Assert.Null(channels["music"].CaptureGain);
        Assert.DoesNotContain(fake.Calls, c => c.Contains(ProRaw, StringComparison.Ordinal) || c.Contains(Dock, StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("pactl set-", StringComparison.Ordinal));
        // The ALSA floor is read once per source.
        Assert.Single(fake.Calls, c => c == "amixer -c 3 contents");

        // Nothing moved in the registry: no helper runs and nothing is reported.
        fake.Clear();
        Assert.False(fake.Mixer.SyncCaptureGains());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void AFailedHardwareRangeProbeRecoversWithoutAVolumeChange()
    {
        using var fake = new FakeAudio();
        var time = new ManualTime();
        fake.Mixer.Time = time;
        fake.FailProbe(true);
        Assert.True(fake.Mixer.SyncCaptureGains());
        Assert.Equal(-6, fake.Mixer.Snapshot().Channels.Single(c => c.Id == "brio").CaptureGain!.MinDb);
        fake.FailProbe(false);
        fake.Clear();
        time.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.False(fake.Mixer.SyncCaptureGains());
        Assert.Empty(fake.Calls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(fake.Mixer.SyncCaptureGains());
        Assert.Equal(18, fake.Mixer.Snapshot().Channels.Single(c => c.Id == "brio").CaptureGain!.MinDb);
        Assert.Single(fake.Calls, c => c == "amixer -c 3 contents");
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("pactl set-", StringComparison.Ordinal));
        fake.Clear();
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(fake.Mixer.SyncCaptureGains());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void FailedHardwareProbesAreRateLimitedAndAnAbsentSourceStopsRetrying()
    {
        using var fake = new FakeAudio();
        var time = new ManualTime();
        fake.Mixer.Time = time;
        fake.FailProbe(true);
        fake.Mixer.SyncCaptureGains();
        fake.Clear();
        for (int i = 0; i < 9; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(fake.Mixer.SyncCaptureGains());
        }
        Assert.Empty(fake.Calls);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(fake.Mixer.SyncCaptureGains());
        Assert.Single(fake.Calls, c => c == "amixer -c 3 contents");
        fake.Clear();
        fake.Gone();
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(fake.Mixer.SyncCaptureGains());
        Assert.Null(fake.Mixer.Snapshot().Channels.Single(c => c.Id == "brio").CaptureGain);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("amixer", StringComparison.Ordinal));
        fake.Clear();
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(fake.Mixer.SyncCaptureGains());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void ASuccessfulProbeWithoutAMatchingRangeIsStillCached()
    {
        using var fake = new FakeAudio();
        var time = new ManualTime();
        fake.Mixer.Time = time;
        fake.Controls("no matching capture volume control");
        Assert.True(fake.Mixer.SyncCaptureGains());
        fake.Clear();
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(fake.Mixer.SyncCaptureGains());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void VolumeChangesDoNotBypassOrPostponeTheFailedProbeDelay()
    {
        using var fake = new FakeAudio();
        var time = new ManualTime();
        fake.Mixer.Time = time;
        fake.FailProbe(true);
        fake.Mixer.SyncCaptureGains();
        fake.MoveBrio(32846);
        fake.Clear();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(fake.Mixer.SyncCaptureGains());
        Assert.Equal(36, fake.Mixer.Snapshot().Channels.Single(c => c.Id == "brio").CaptureGain!.GainDb, 1);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("amixer", StringComparison.Ordinal));
        fake.FailProbe(false);
        fake.Clear();
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.True(fake.Mixer.SyncCaptureGains());
        Assert.Equal(18, fake.Mixer.Snapshot().Channels.Single(c => c.Id == "brio").CaptureGain!.MinDb);
        Assert.Single(fake.Calls, c => c == "amixer -c 3 contents");
    }

    [Fact]
    public void AChangeMadeElsewhereIsReportedAndNeverWrittenBack()
    {
        using var fake = new FakeAudio();
        fake.Mixer.SyncCaptureGains();
        fake.Clear();
        // A voice chat's automatic gain lowers the BRIO: the registry shows it, pactl reads it.
        fake.MoveBrio(32846);
        Assert.True(fake.Mixer.SyncCaptureGains());
        Assert.Equal(36, fake.Mixer.Snapshot().Channels.Single(c => c.Id == "brio").CaptureGain!.GainDb, 1);
        Assert.Contains("pactl --format=json list sources", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("pactl set-", StringComparison.Ordinal));
        fake.Clear();
        Assert.False(fake.Mixer.SyncCaptureGains());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void SettingTheGainWritesTheSourceVolumeAndOnlyOnAnInputThatHasOne()
    {
        using var fake = new FakeAudio();
        fake.Mixer.SetCaptureGain("brio", 36);
        Assert.Contains($"pactl set-source-volume {Brio} 32846", fake.Calls);
        Assert.Equal(36, fake.Mixer.Snapshot().Channels.Single(c => c.Id == "brio").CaptureGain!.GainDb, 1);
        // Past the top is the top.
        fake.Clear();
        fake.Mixer.SetCaptureGain("brio", 80);
        Assert.Contains($"pactl set-source-volume {Brio} 65536", fake.Calls);
        // A software level writes the level itself.
        fake.Mixer.SetCaptureGain("onboard", -6);
        Assert.Contains($"pactl set-source-volume {Onboard} {CaptureGain.RawVolume(-6)}", fake.Calls);

        fake.Clear();
        foreach (string unit in new[] { "pro", "dock", "unit" })
            Assert.Throws<InvalidOperationException>(() => fake.Mixer.SetCaptureGain(unit, 30));
        Assert.Throws<ArgumentException>(() => fake.Mixer.SetCaptureGain("music", 30));
        Assert.Throws<ArgumentException>(() => fake.Mixer.SetCaptureGain("brio", double.NaN));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("pactl set-", StringComparison.Ordinal));
        // A missing source is refused too.
        fake.Gone();
        Assert.Throws<InvalidOperationException>(() => fake.Mixer.SetCaptureGain("brio", 30));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("pactl set-", StringComparison.Ordinal));
    }

    [Fact]
    public void ACaptureInputCarryingAUnitsInputHasNoGainOfItsOwn()
    {
        MethodInfo source = typeof(Mixer).GetMethod("CaptureGainSource", BindingFlags.Static | BindingFlags.NonPublic)!;
        var channel = new ChannelDefinition("mic", "Mic") { CaptureSource = Brio };
        Assert.Equal(Brio, source.Invoke(null, [channel, new Dictionary<string, UnitInputStatus>()]));
        Assert.Null(source.Invoke(null, [channel, new Dictionary<string, UnitInputStatus> { ["mic"] = new("0fd9:0070", 1, true) }]));
        Assert.Null(source.Invoke(null, [channel with { CaptureUnit = "0fd9:0070" }, new Dictionary<string, UnitInputStatus>()]));
    }

    [Theory]
    [InlineData("brio", "36", null)]
    [InlineData("brio", "-12.5", null)]
    [InlineData("brio", "\"36\"", "setCaptureGain: 'value' must be a finite number")]
    [InlineData("music", "36", "setCaptureGain: need a capture input channel")]
    [InlineData("xlr1", "36", "setCaptureGain: need a capture input channel")]
    [InlineData(null, "36", "setCaptureGain: need a capture input channel")]
    public void TheCommandNeedsACaptureInputAndANumber(string? channel, string value, string? error)
    {
        var command = System.Text.Json.JsonSerializer.Deserialize<Command>(
            $$"""{"cmd":"setCaptureGain","channel":{{(channel is null ? "null" : $"\"{channel}\"")}},"value":{{value}}}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        string? result = CommandValidation.Check(command, new Layout(), _ => null);
        if (error is null) Assert.Null(result);
        else Assert.StartsWith(error, result, StringComparison.Ordinal);
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "brio" or "music" or "xlr1";
        public bool HasMix(string id) => id == "monitor";
        public bool HasCaptureChannel(string id) => id == "brio";
        public bool IsMonitorFeed(string feed) => false;
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => false;
        public int OverrideCount => 0;
    }

    /// <summary>
    /// A mixer over fake pactl, pw-dump and amixer: capture inputs on the
    /// BRIO, the onboard input, the Pro's raw source and a dock, one that
    /// remembers a unit, and an application channel.
    /// </summary>
    private sealed class FakeAudio : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-capture-gain-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        public Mixer Mixer { get; }
        public string[] Calls => File.Exists(Path.Combine(_dir, "calls")) ? File.ReadAllLines(Path.Combine(_dir, "calls")) : [];
        public void Clear() => File.WriteAllText(Path.Combine(_dir, "calls"), "");
        public void FailProbe(bool fail)
        {
            string path = Path.Combine(_dir, "fail-probe");
            if (fail) File.WriteAllText(path, "");
            else File.Delete(path);
        }
        public void Controls(string controls) => File.WriteAllText(Path.Combine(_dir, "controls"), controls);

        public FakeAudio()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                echo "pactl $*" >> "$dir/calls"
                case "$*" in
                    '--format=json list sources') /bin/cat "$dir/sources" ;;
                esac
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(_dir, "amixer"), """
                dir=${0%/*}
                echo "amixer $*" >> "$dir/calls"
                [ -e "$dir/fail-probe" ] && exit 1
                [ "$*" = '-c 3 contents' ] && /bin/cat "$dir/controls"
                """);
            File.WriteAllText(Path.Combine(_dir, "sources"), Sources());
            File.WriteAllText(Path.Combine(_dir, "controls"), BrioControls);
            Graph(1.0);
            Environment.SetEnvironmentVariable("PATH", _dir);
            Mixer = new(new PipeWireAdapter());
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, new MixerConfig
            {
                Channels = [new("xlr1", "Mic 1") { InputPair = 0 },
                    new("brio", "Brio") { CaptureSource = Brio },
                    new("onboard", "Onboard") { CaptureSource = Onboard },
                    new("pro", "Pro raw") { CaptureSource = ProRaw, CaptureMonoChannel = 2 },
                    new("dock", "Dock") { CaptureSource = Dock },
                    new("unit", "Unit") { CaptureSource = "alsa_input.usb-renamed", CaptureUnit = "0fd9:0070" },
                    new("music", "Music")],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
            });
            Clear();
        }

        /// <summary>The registry's view of the BRIO's volume, linear as PipeWire keeps it.</summary>
        private void Graph(double brio)
        {
            static string Node(int id, string name, string level) =>
                "{\"id\":" + id + ",\"type\":\"PipeWire:Interface:Node\",\"info\":{\"props\":{\"node.name\":\"" + name
                + "\",\"media.class\":\"Audio/Source\"},\"params\":{\"Props\":[{\"volume\":1.0,\"mute\":false,\"channelVolumes\":["
                + level + "," + level + "]}]}}}";
            File.WriteAllText(Path.Combine(_dir, "graph"), "[" + Node(50, Brio, brio.ToString(System.Globalization.CultureInfo.InvariantCulture))
                + "," + Node(51, Onboard, "0.125895") + "]");
        }

        /// <summary>Another program moves the BRIO's volume to a raw Pulse value.</summary>
        public void MoveBrio(long raw)
        {
            File.WriteAllText(Path.Combine(_dir, "sources"), Sources(raw));
            Graph(Math.Round(Math.Pow(raw / 65536.0, 3), 6));
            // The adapter keeps a one-shot dump for a moment; a daemon's registry has no such delay.
            Thread.Sleep(450);
        }

        /// <summary>The BRIO is unplugged.</summary>
        public void Gone() => File.WriteAllText(Path.Combine(_dir, "sources"), "[" + Source(Onboard, 32846, 65536, false, "2") + "]");

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally { Environment.SetEnvironmentVariable("PATH", _path); Directory.Delete(_dir, true); }
        }
    }
}
