using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Mic setup's noise removal model, with no window: it shows what the daemon
/// reports, sends one <c>setNoiseRemoval</c> per change with only the field
/// that changed, offers DeepFilterNet only when the helper carries it, keeps
/// a fresh edit against an older report, and says what runs or why not.
/// </summary>
[Collection("xdg-config")]
public sealed class NoiseRemovalModelTests
{
    private sealed class Recorder : INoiseCommands
    {
        public readonly ConcurrentQueue<(string Channel, bool? Enabled, string? Engine, double? Strength)> Sent = new();

        public Task SetNoiseRemovalAsync(string channel, bool? enabled = null, string? engine = null, double? strength = null)
        {
            Sent.Enqueue((channel, enabled, engine, strength));
            return Task.CompletedTask;
        }
    }

    private static JsonNode State(bool enabled = false, string engine = "rnnoise", double strength = 100,
        string engines = """["rnnoise","deepfilternet"]""", string? active = null, double? latency = null,
        bool available = true, string? error = null)
        => JsonNode.Parse($$"""
            {"enabled":{{(enabled ? "true" : "false")}},"engine":"{{engine}}","strength":{{strength}},"engines":{{engines}},
             "active":{{(active is null ? "null" : $"\"{active}\"")}},"latencyMs":{{(latency is double ms ? ms.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},
             "available":{{(available ? "true" : "false")}},"error":{{(error is null ? "null" : $"\"{error}\"")}}}
            """)!;

    [Fact]
    public void ItShowsWhatTheDaemonReportsAndSendsOnlyWhatChanged() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var time = new ManualTime();
        var noise = new MicNoiseViewModel(sent, "xlr1", "XLR 1", time);
        noise.Apply(State(enabled: true, engine: "deepfilternet", strength: 30, active: "deepfilternet", latency: 55.3));
        Assert.True(noise.Enabled);
        Assert.True(noise.IsDeepFilterNet);
        Assert.False(noise.IsRnNoise);
        Assert.Equal(30, noise.Strength);
        Assert.True(noise.HasDeepFilterNet);
        Assert.False(noise.LacksDeepFilterNet);
        Assert.Equal(Localizer.Format("NoiseRemovalRunning", Localizer.Text("NoiseRemovalDeepFilterNet"), "55"), noise.Status);
        Assert.True(noise.ShowsReport);
        Assert.Empty(sent.Sent);

        noise.ChooseEngine("rnnoise");
        Assert.True(sent.Sent.TryDequeue(out var change));
        Assert.Equal(("xlr1", (bool?)null, "rnnoise", (double?)null), change);
        // A report from before the change does not move the choice back.
        noise.Apply(State(enabled: true, engine: "deepfilternet", strength: 30, active: "deepfilternet", latency: 55.3));
        Assert.True(noise.IsRnNoise);
        time.Advance(MicNoiseViewModel.EchoWait);
        noise.Apply(State(enabled: true, engine: "rnnoise", strength: 30, active: "rnnoise", latency: 30));
        Assert.True(noise.IsRnNoise);
        Assert.Equal(Localizer.Format("NoiseRemovalRunning", Localizer.Text("NoiseRemovalRnNoise"), "30"), noise.Status);

        noise.Enabled = false;
        Assert.True(sent.Sent.TryDequeue(out change));
        Assert.Equal(("xlr1", (bool?)false, (string?)null, (double?)null), change);
        Assert.Equal("", noise.Status);
        Assert.Empty(sent.Sent);
    });

    [Fact]
    public void StrengthSnapsAndReadsAsDecibelsOrNoLimit() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var noise = new MicNoiseViewModel(sent, "guest", "Guest", new ManualTime());
        noise.Apply(State());
        Assert.Equal(100, noise.Strength);
        Assert.Equal(Localizer.Text("NoiseRemovalUnlimited"), noise.StrengthText);
        noise.Strength = 24.4;
        Assert.Equal(24, noise.Strength);
        Assert.Equal(Localizer.Format("NoiseRemovalStrengthValue", 24), noise.StrengthText);
        noise.Strength = -5;
        Assert.Equal(0, noise.Strength);
        SliderSync.FlushPending();
        Assert.True(sent.Sent.TryDequeue(out var change));
        Assert.Equal(("guest", (bool?)null, (string?)null, (double?)0), change);
        Assert.Empty(sent.Sent);
    });

    [Fact]
    public void DeepFilterNetIsOfferedOnlyWhenTheHelperCarriesIt() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var noise = new MicNoiseViewModel(sent, "xlr1", "XLR 1", new ManualTime());
        noise.Apply(State(engines: """["rnnoise"]"""));
        Assert.False(noise.HasDeepFilterNet);
        Assert.True(noise.LacksDeepFilterNet);
        noise.ChooseEngine("deepfilternet");
        Assert.Empty(sent.Sent);
        Assert.True(noise.IsRnNoise);
        // Without a helper nothing is offered, and nothing says DeepFilterNet is missing.
        noise.Apply(State(engines: "[]", available: false));
        Assert.False(noise.Available);
        Assert.False(noise.LacksDeepFilterNet);
    });

    [Fact]
    public void ItSaysWhyItDoesNotRunAndThatItIsStarting() => TestUi.Run(() =>
    {
        var noise = new MicNoiseViewModel(new Recorder(), "xlr1", "XLR 1", new ManualTime());
        noise.Apply(State(enabled: true, engine: "deepfilternet"));
        Assert.Equal(Localizer.Text("NoiseRemovalLoading"), noise.Status);
        Assert.True(noise.ShowsReport);
        noise.Apply(State(enabled: true, error: "Noise removal runs at 48 kHz."));
        Assert.Equal("Noise removal runs at 48 kHz.", noise.Status);
        Assert.True(noise.HasError);
        Assert.False(noise.ShowsReport);
        noise.Apply(State(enabled: true));
        Assert.Equal(Localizer.Text("NoiseRemovalStarting"), noise.Status);
        Assert.Equal(Localizer.Format("NoiseRemovalSwitchName", "XLR 1"), noise.SwitchName);
    });

    [Fact]
    public void TheDenoiseButtonLightsWhileItRunsAndWarnsWhenItCannot() => TestUi.Run(() =>
    {
        var time = new ManualTime();
        var noise = new MicNoiseViewModel(new Recorder(), "xlr1", "XLR 1", time);
        var raised = new List<string?>();
        noise.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        noise.Apply(State());
        Assert.False(noise.IsLit);
        Assert.False(noise.IsFaulty);
        Assert.Equal(Localizer.Format("NoiseButtonNameOff", "XLR 1"), noise.ButtonName);

        noise.Enabled = true;
        Assert.True(noise.IsLit);
        Assert.Contains(nameof(MicNoiseViewModel.IsLit), raised);
        Assert.Equal(Localizer.Format("NoiseButtonNameOn", "XLR 1"), noise.ButtonName);

        // On and refused, or on with no helper: the alert colour, not the lit one.
        noise.Apply(State(enabled: true, error: "Noise removal runs at 48 kHz."));
        Assert.False(noise.IsLit);
        Assert.True(noise.IsFaulty);
        Assert.Equal(Localizer.Format("NoiseButtonNameUnavailable", "XLR 1"), noise.ButtonName);
        noise.Apply(State(enabled: true, engines: "[]", available: false));
        Assert.True(noise.IsFaulty);
        // Off, a missing helper is no fault of the button's.
        time.Advance(MicNoiseViewModel.EchoWait);
        noise.Apply(State(engines: "[]", available: false));
        Assert.False(noise.IsFaulty);
        Assert.False(noise.IsLit);
    });
}
