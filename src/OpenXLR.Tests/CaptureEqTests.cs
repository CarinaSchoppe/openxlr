using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone EQ on capture inputs: a capture input on one mono port
/// carries the same EQ as XLR 1 and XLR 2, which always runs on this
/// computer. The EQ commands take its id, setEqProcessing refuses it, the
/// state reports it with processing software and no unit, the settings and
/// a profile carry it, and an additional unit's microphone on a capture input
/// is never written an EQ. A capture input on a stereo pair has none.
/// </summary>
public sealed class CaptureEqTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Extra = "0fd9:00c7@aaaaaaaaaaaaaaaa";

    private static readonly ChannelDefinition Guest = new("guest", "Guest") { CaptureSource = "usb_guest", CaptureMonoChannel = 0 };
    private static readonly ChannelDefinition Camera = new("camera", "Camera") { CaptureSource = "usb_camera", CapturePair = 0 };

    /// <summary>The default layout with a mono and a stereo capture input, never built.</summary>
    private static Mixer WithCaptures(params ChannelDefinition[] captures)
    {
        var mixer = new Mixer();
        typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer,
            MixerConfig.Default() with { Channels = [.. MixerConfig.Default().Channels, .. captures] });
        return mixer;
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system" or "guest" or "camera";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest" or "camera";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public bool EqRunsInSoftwareOnly(string id) => id is "guest";
        public int OverrideCount => 0;
    }

    private static string Bands() => JsonSerializer.Serialize(MicEq.Default.Bands, Web);

    [Theory]
    [InlineData("""{"cmd":"setEqEnabled","channel":"guest","value":true}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":6}""", null)]
    [InlineData("""{"cmd":"setEq","channel":"guest","eq":{"enabled":true,"bands":BANDS}}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"guest","band":17,"gain":6}""", "setEqBand: need 'band' from 1 to 16")]
    [InlineData("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":13}""", "setEqBand: gain must be -12 to +12 dB")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"missing","value":true}""", "setEqEnabled: 'missing' is not a microphone input")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"system","value":true}""", "setEqEnabled: 'system' is not a microphone input")]
    [InlineData("""{"cmd":"setEq","channel":"camera","eq":{"enabled":true,"bands":BANDS}}""", "setEq: 'camera' is not a microphone input")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"guest","value":"software"}""", "setEqProcessing: 'guest' is a capture input, whose EQ always runs on this computer")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"guest","value":"device"}""", "setEqProcessing: 'guest' is a capture input, whose EQ always runs on this computer")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"xlr1","value":"software"}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","channel":"xlr2","value":true}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"aux","band":2,"gain":6}""", "setEqBand: 'aux' is not a microphone input")]
    public void TheEqCommandsTakeACaptureMicrophone(string json, string? error)
    {
        var cmd = JsonSerializer.Deserialize<Command>(json.Replace("BANDS", Bands()), Web)!;
        Assert.Equal(error, CommandValidation.Check(cmd, new Layout(), _ => null, nativeHostInstalled: true));
    }

    [Fact]
    public void SwitchingACaptureEqOnNeedsTheNativeHelper()
    {
        var on = JsonSerializer.Deserialize<Command>("""{"cmd":"setEqEnabled","channel":"guest","value":true}""", Web)!;
        Assert.Equal(Mixer.EqNeedsHost, CommandValidation.Check(on, new Layout(), _ => null, nativeHostInstalled: false));
        var band = JsonSerializer.Deserialize<Command>("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":3}""", Web)!;
        Assert.Null(CommandValidation.Check(band, new Layout(), _ => null, nativeHostInstalled: false));
    }

    /// <summary>The mixer answers the validator for its own layout: the mono capture input yes, the stereo pair and an application channel no.</summary>
    [Fact]
    public void TheMixerNamesItsCaptureMicrophones()
    {
        using Mixer mixer = WithCaptures(Guest, Camera);
        Assert.True(mixer.IsMicrophoneChannel("xlr1"));
        Assert.True(mixer.IsMicrophoneChannel("guest"));
        Assert.False(mixer.IsMicrophoneChannel("camera"));
        Assert.False(mixer.IsMicrophoneChannel("system"));
        Assert.False(mixer.IsMicrophoneChannel("missing"));
        Assert.True(mixer.EqRunsInSoftwareOnly("guest"));
        Assert.False(mixer.EqRunsInSoftwareOnly("xlr1"));
        Assert.False(mixer.EqRunsInSoftwareOnly("camera"));
        string? Check(string json) => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, mixer, _ => null, nativeHostInstalled: true);
        Assert.Null(Check("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":6}"""));
        Assert.Equal("setEqBand: 'camera' is not a microphone input", Check("""{"cmd":"setEqBand","channel":"camera","band":2,"gain":6}"""));
        Assert.Equal(Mixer.EqSoftwareOnly("guest"), Check("""{"cmd":"setEqProcessing","channel":"guest","value":"software"}"""));
        Assert.Null(Check("""{"cmd":"setEqProcessing","channel":"xlr1","value":"software"}"""));
    }

    [Fact]
    public void ACaptureMicrophoneReportsASoftwareEqAndAStereoPairNone()
    {
        using Mixer mixer = WithCaptures(Guest, Camera);
        MixerState state = mixer.Snapshot();
        MicEqState eq = state.Channels.Single(c => c.Id == "guest").Eq!;
        Assert.False(eq.Enabled);
        Assert.Equal(MicEq.Default.Bands, eq.Bands);
        Assert.Equal(MicEqProcessing.Software, eq.Processing);
        Assert.Null(eq.Device);
        Assert.Null(eq.Backend);
        Assert.Null(state.Channels.Single(c => c.Id == "camera").Eq);
        Assert.Null(state.Channels.Single(c => c.Id == "system").Eq);
        Assert.Equal(MicEqProcessing.Device, state.Channels.Single(c => c.Id == "xlr1").Eq!.Processing);

        using JsonDocument json = JsonSerializer.SerializeToDocument(state, Web);
        JsonElement guest = json.RootElement.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "guest");
        Assert.Equal("software", guest.GetProperty("eq").GetProperty("processing").GetString());
        Assert.Equal(JsonValueKind.Null, guest.GetProperty("eq").GetProperty("device").ValueKind);
    }

    /// <summary>
    /// The mixer keeps a capture input's EQ as it keeps XLR 1's: snapped, in
    /// the saved settings, in a profile, which names every microphone, and
    /// refuses a choice of where it runs.
    /// </summary>
    [Fact]
    public void ACaptureEqIsStoredSavedAndNamedInAProfile()
    {
        using Mixer mixer = WithCaptures(Guest, Camera);
        mixer.SetEqBand("guest", 2, new(Gain: 6.1, Frequency: 1000.4));
        mixer.SetEqEnabled("guest", true);
        MicEq eq = mixer.EqFor("guest");
        Assert.True(eq.Enabled);
        Assert.Equal(6, eq.Bands[1].Gain);
        Assert.Equal(1000, eq.Bands[1].Frequency);
        Assert.True(mixer.EqWantsSoftwareStage("guest"));
        Assert.Equal(MicEqProcessing.Software, mixer.EqProcessingFor("guest"));
        Assert.Equal(eq, mixer.ExportSettings().Eq["guest"]);
        Assert.Equal(["guest", "xlr1", "xlr2"], mixer.ExportScene().Eq!.Keys.Order());
        Assert.Equal(eq, mixer.ExportScene().Eq!["guest"]);

        Assert.Equal(Mixer.EqSoftwareOnly("guest"), Assert.Throws<ArgumentException>(() => mixer.SetEqProcessing("guest", "software")).Message);
        Assert.Throws<ArgumentException>(() => mixer.SetEq("camera", MicEq.Default));
        Assert.Throws<ArgumentException>(() => mixer.SetEqEnabled("system", true));
        Assert.False(mixer.ExportSettings().EqProcessing.ContainsKey("guest"));
    }

    /// <summary>A capture input moved to another mono port keeps its EQ; one moved to a stereo pair keeps it saved and offers none.</summary>
    [Fact]
    public void ASourceChangeKeepsTheEq()
    {
        using Mixer mixer = WithCaptures(Guest);
        mixer.SetEqEnabled("guest", true);
        mixer.SetEqBand("guest", 3, new(Gain: -4));
        MicEq eq = mixer.EqFor("guest");
        MixerSettings? saved = null;
        mixer.SetCaptureSource("guest", "usb_other", 0, s => { saved = s; return null; }, monoChannel: 1);
        Assert.Equal(eq, mixer.EqFor("guest"));
        Assert.Equal(eq, saved!.Eq["guest"]);
        Assert.NotNull(mixer.Snapshot().Channels.Single(c => c.Id == "guest").Eq);
        mixer.SetCaptureSource("guest", "usb_other", 0, _ => null);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "guest").Eq);
        Assert.False(mixer.EqWantsSoftwareStage("guest"));
        Assert.Equal(eq, mixer.ExportSettings().Eq["guest"]);
        mixer.SetCaptureSource("guest", "usb_other", 0, _ => null, monoChannel: 0);
        MicEqState back = mixer.Snapshot().Channels.Single(c => c.Id == "guest").Eq!;
        Assert.Equal(eq, new MicEq { Enabled = back.Enabled, Bands = back.Bands });
    }

    /// <summary>
    /// An additional unit's microphone on a capture input: the unit's model
    /// runs the EQ itself, but the capture input's EQ stays on this computer,
    /// the state names no unit, and nothing is written to the unit.
    /// </summary>
    [Fact]
    public void AnAdditionalUnitsMicrophoneOnACaptureInputIsNeverWrittenAnEq()
    {
        var port = new Port();
        ChannelDefinition extra = new("extra", "Extra") { CaptureSource = "alsa_input.usb-Elgato_Wave_XLR_Dock_MK2-00.mono", CaptureMonoChannel = 0, CaptureUnit = Extra };
        using Mixer mixer = WithCaptures(extra);
        mixer.SetAttachedUnits([new AttachedUnit(Extra, ["Elgato_Wave_XLR_Dock_MK2"], 1, true, true)]);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Extra] = new("Wave XLR Dock MK.2", "1.0.15", true) });
        Assert.Equal(Extra, mixer.Snapshot().Channels.Single(c => c.Id == "extra").UnitInput?.Unit);
        mixer.SetEqEnabled("extra", true);
        mixer.SetEqBand("extra", 2, new(Gain: 6));
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqRunsOnDevice("extra"));
        Assert.True(mixer.EqWantsSoftwareStage("extra"));
        MicEqState eq = mixer.Snapshot().Channels.Single(c => c.Id == "extra").Eq!;
        Assert.Null(eq.Device);
        Assert.Equal(MicEqProcessing.Software, eq.Processing);
        Assert.Empty(port.Posts);
    }

    private sealed class Port : IDeviceEqPort
    {
        public readonly List<(string Unit, int Input, DeviceEq Eq)> Posts = [];
        public void Post(string unit, int input, DeviceEq eq, string owner) => Posts.Add((unit, input, eq));
        public DeviceEqStatus Status(string unit, int input) => DeviceEqStatus.None;
    }
}
