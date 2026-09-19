using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class SoundCheckTests
{
    [Theory]
    [InlineData("xlr1", "record", true)]
    [InlineData("xlr2", "loop", true)]
    [InlineData("xlr1", "live", true)]
    [InlineData("xlr1", "stop", true)]
    [InlineData("system", "record", false)]
    [InlineData("mix:monitor", "record", false)]
    [InlineData("xlr1", "unknown", false)]
    [InlineData("xlr1", null, false)]
    public void OnlyExplicitMicrophoneActionsAreAccepted(string channel, string? action, bool valid)
    {
        using var mixer = new Mixer();
        Assert.Equal(valid, CommandValidation.Check(new Command { Cmd = "soundCheck", Channel = channel, Action = action }, mixer, _ => null) is null);
    }

    [Fact]
    public void NoDeviceDoesNotCreateARecordingAndStopIsIdempotent()
    {
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr1", "record"));
        Assert.Throws<ArgumentException>(() => mixer.SoundCheck("system", "record"));
        Assert.Throws<ArgumentException>(() => mixer.SoundCheck("xlr1", "invalid"));
        mixer.SoundCheck("xlr1", "stop");
        mixer.SoundCheck("xlr1", "stop");
        Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
        Assert.Null(mixer.Snapshot().SoundCheck.Channel);
        Assert.DoesNotContain("soundCheck", System.Text.Json.JsonSerializer.Serialize(mixer.ExportSettings()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheViewUsesServerProgressAndNeverLoopsAnEmptySample()
    {
        await using var client = new DaemonClient();
        var view = new SoundCheckViewModel(client, "xlr1");
        Assert.True(view.CanRecord);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"recording","seconds":0.05}"""));
        Assert.False(view.CanRecord);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"recording","seconds":1.5}"""));
        Assert.True(view.CanLoop);
        Assert.Contains("Recording", view.Status);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5}"""));
        Assert.Contains("Looping", view.Status);
        Assert.True(view.CanStop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr2","mode":"looping","seconds":1.5}"""));
        Assert.False(view.Active);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5}"""));
        view.Apply(null);
        Assert.False(view.CanLoop);
        Assert.False(view.CanStop);
        Assert.Contains("No sample", view.Status);
        await view.RunAsync("record");
        Assert.NotNull(view.Error);
    }
}
