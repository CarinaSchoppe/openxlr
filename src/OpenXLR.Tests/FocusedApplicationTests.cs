using System.Text;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class FocusedApplicationTests
{
    private static AudioStream App(int pid, string binary, string? name = null)
        => new(1, name ?? binary, binary, null) { ProcessId = pid };

    [Fact]
    public void ExactProcessWinsAndAUniqueAudioChildCanIdentifyABrowser()
    {
        AudioStream[] streams = [App(100, "browser"), App(101, "other"), App(100, "browser")];
        Assert.Equal("browser", FocusedApplication.Resolve(100, streams, _ => 100));
        Assert.Equal("browser", FocusedApplication.Resolve(99, [App(101, "browser")], p => p == 101 ? 100 : 99));
        Assert.Equal("balatro", FocusedApplication.Resolve(50, [App(50, "wine64-preloader", "Balatro.exe")], _ => null));
    }

    [Fact]
    public void AmbiguousMissingAndCyclicProcessesNeverGuessAnApplication()
    {
        Assert.Throws<InvalidOperationException>(() => FocusedApplication.Resolve(0, [], _ => null));
        Assert.Throws<InvalidOperationException>(() => FocusedApplication.Resolve(100, [App(100, "one"), App(100, "two")], _ => null));
        Assert.Throws<InvalidOperationException>(() => FocusedApplication.Resolve(100, [App(101, "one"), App(102, "two")], _ => 100));
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => FocusedApplication.Resolve(100, [App(101, "one")], p => { calls++; return p; }));
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => FocusedApplication.Resolve(100, [App(101, "one")], _ => null));
    }

    [Theory]
    [InlineData("(uint32 123,)", 123)]
    [InlineData(" (uint32 42,)\n", 42)]
    [InlineData("(uint32 0,)", 0)]
    [InlineData("(uint32 -1,)", 0)]
    [InlineData("(uint32 999999999999,)", 0)]
    [InlineData("garbage", 0)]
    public void DesktopRepliesAreParsedStrictly(string text, int expected)
    {
        if (expected > 0) Assert.Equal(expected, DesktopFocusQuery.Parse(text));
        else Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.Parse(text));
    }

    [Fact]
    public void LiveClientProcessIdsTakePrecedenceOverPlaybackMetadata()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            [{"id":1,"type":"PipeWire:Interface:Client","info":{"props":{"application.name":"Browser","application.process.binary":"browser","application.process.id":"123"}}},
             {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"client.id":1,"media.class":"Stream/Output/Audio","application.process.id":999}}}]
            """);
        Assert.Equal(123, Assert.Single(PipeWireAdapter.ListClients(json)).ProcessId);
        Assert.Equal(123, Assert.Single(PipeWireAdapter.ListStreams(json)).ProcessId);
    }

    [Fact]
    public void ShortcutSettingsDropInvalidAndDuplicateIdsAndBoundTheirCount()
    {
        var settings = new DesktopKeySettings { Enabled = true, FocusChannels = [null!, "", "a/b", "valid", "valid", .. Enumerable.Range(0, 40).Select(i => "channel" + i)] }.Normalize();
        Assert.Equal(32, settings.FocusChannels.Count);
        Assert.Equal("valid", settings.FocusChannels[0]);
        Assert.All(settings.FocusChannels, id => Assert.True(DesktopKeySettings.ValidId(id)));
    }
}
