using System.Text;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class FocusedApplicationTests
{
    [Theory]
    [InlineData("('42',)", true)]
    [InlineData("('42,73,42',)", true)]
    [InlineData("('2@123',)", true)]
    [InlineData("('2',)", false)]
    [InlineData("('99',)", false)]
    [InlineData("('42;73',)", false)]
    [InlineData("('',)", false)]
    [InlineData("garbage", false)]
    public void DesktopRepliesContainOnlyRequestedAudioProcessKeys(string text, bool accepted)
    {
        string[] candidates = ["42", "73", "2@123"];
        if (accepted) Assert.All(DesktopFocusQuery.ParseAudioProcesses(text, candidates), key => Assert.Contains(key, candidates));
        else Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ParseAudioProcesses(text, candidates));
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

    [Theory]
    [InlineData("\"123\"", "2@123")]
    [InlineData("123", "2@123")]
    [InlineData("\"invalid\"", "2@invalid")]
    [InlineData("null", "2@invalid")]
    public void PortalInstanceMetadataIsTakenFromTheClientNotTheStream(string instance, string expected)
    {
        byte[] graph = Encoding.UTF8.GetBytes("""
            [{"id":1,"type":"PipeWire:Interface:Client","info":{"props":{"application.process.binary":"browser","application.process.id":"2","pipewire.access.portal.instance_id":INSTANCE}}},
             {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"client.id":1,"media.class":"Stream/Output/Audio","application.process.id":"42","pipewire.access.portal.instance_id":"456"}}}]
            """.Replace("INSTANCE", instance));
        Assert.Equal(expected, Assert.Single(PipeWireAdapter.ListClients(graph)).FocusProcessKey);
        Assert.Equal(expected, Assert.Single(PipeWireAdapter.ListStreams(graph)).FocusProcessKey);
    }

    [Fact]
    public void APortalClientWithNoInstanceIsNeverTreatedAsANativeProcess()
    {
        byte[] graph = Encoding.UTF8.GetBytes("""
            [{"id":1,"type":"PipeWire:Interface:Client","info":{"props":{"application.process.binary":"browser","application.process.id":"2","pipewire.access.portal.app_id":"org.chromium.Chromium"}}}]
            """);
        Assert.Equal("2@invalid", Assert.Single(PipeWireAdapter.ListClients(graph)).FocusProcessKey);
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
