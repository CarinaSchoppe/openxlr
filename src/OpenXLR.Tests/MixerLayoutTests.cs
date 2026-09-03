using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MixerLayoutTests
{
    [Fact]
    public void OlderSettingsWithoutLayoutKeepWaveLinkDefaults()
    {
        MixerConfig config = MixerConfig.FromSettings(new MixerSettings());

        Assert.Contains(config.Channels, c => c.Id == "game");
        Assert.Contains(config.Channels, c => c.Id == "system");
        Assert.Contains(config.Mixes, m => m.Id == "stream" && m.Kind == MixKind.VirtualMic);
        Assert.Contains(config.Mixes, m => m.Id == "chat" && m.Kind == MixKind.VirtualMic);
    }

    [Fact]
    public void SavedLayoutReplacesOnlyUserManagedNodes()
    {
        var settings = new MixerSettings
        {
            UserChannels = [new UserChannelDefinition("podcast", "Podcast")],
            UserMixes = [new UserMixDefinition("recording", "Recording")],
        };

        MixerConfig config = MixerConfig.FromSettings(settings);

        Assert.Equal(["xlr1", "xlr2", "aux", "podcast"], config.Channels.Select(c => c.Id));
        Assert.Equal(["monitor", "recording", "auxout"], config.Mixes.Select(m => m.Id));
        Assert.All(config.Mixes, mix =>
            Assert.True(config.Channels.Single(c => c.Id == "podcast").Levels.ContainsKey(mix.Id)));
        Assert.Equal(MixKind.VirtualMic, config.Mixes.Single(m => m.Id == "recording").Kind);
    }

    [Fact]
    public void ExplicitlyEmptyVirtualMixListIsPreserved()
    {
        MixerConfig config = MixerConfig.FromSettings(new MixerSettings
        {
            UserMixes = [],
            UserChannels = [new UserChannelDefinition("system", "System")],
        });

        Assert.DoesNotContain(config.Mixes, m => m.Kind == MixKind.VirtualMic);
        Assert.Contains(config.Mixes, m => m.Kind == MixKind.Monitor);
        Assert.Contains(config.Mixes, m => m.Kind == MixKind.AuxPort);
    }

    [Fact]
    public void EmptyApplicationLayoutHealsToOneSafeSink()
    {
        MixerConfig config = MixerConfig.FromSettings(new MixerSettings { UserChannels = [] });

        ChannelDefinition app = Assert.Single(config.Channels, c => c.InputPair is null);
        Assert.Equal("system", app.Id);
    }

    [Theory]
    [InlineData("Podcast", "podcast")]
    [InlineData("Gaming / Discord", "gaming-discord")]
    [InlineData("Übertragung", "bertragung")]
    [InlineData("123", "channel-123")]
    public void NewIdCreatesSafePipeWireNames(string name, string expected)
        => Assert.Equal(expected, MixerConfig.NewId(name, "channel", []));

    [Fact]
    public void NewIdAvoidsCollisions()
        => Assert.Equal("podcast-2", MixerConfig.NewId("Podcast", "channel", ["podcast"]));

    [Fact]
    public void RenamingDisplayNameKeepsStableLayoutId()
    {
        MixerConfig config = MixerConfig.FromSettings(new MixerSettings
        {
            UserChannels = [new UserChannelDefinition("podcast", "Interview")],
            UserMixes = [new UserMixDefinition("recording", "Archive")],
        });

        Assert.Equal("Interview", config.Channels.Single(c => c.Id == "podcast").Name);
        Assert.Equal("Archive", config.Mixes.Single(m => m.Id == "recording").Name);
    }

    [Fact]
    public void SettingsRoundTripKeepsExplicitEmptyLayout()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-layout-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        try
        {
            new MixerSettings
            {
                UserChannels = [],
                UserMixes = [],
                MonitoredMixId = "monitor",
            }.Save(path);
            MixerSettings loaded = MixerSettings.Load(path)!;

            Assert.NotNull(loaded.UserChannels);
            Assert.Empty(loaded.UserChannels);
            Assert.NotNull(loaded.UserMixes);
            Assert.Empty(loaded.UserMixes);
            Assert.Equal("monitor", loaded.MonitoredMixId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReorderingChannelsKeepsStableIdsAndPipeWireNames()
    {
        using var mixer = new Mixer();
        string[] requested = ["sfx", "voicechat", "system", "browser", "music", "game"];
        var sinkNames = mixer.Config.Channels.ToDictionary(c => c.Id, c => c.SinkName);

        mixer.ReorderUserChannels(requested);

        Assert.Equal(requested, mixer.Config.Channels.Where(c => c.InputPair is null).Select(c => c.Id));
        Assert.Equal(requested, mixer.ExportSettings().UserChannels!.Select(c => c.Id));
        Assert.All(mixer.Config.Channels, channel => Assert.Equal(sinkNames[channel.Id], channel.SinkName));
    }

    [Fact]
    public void ReorderingMixesKeepsStructuralBusesAtTheEdges()
    {
        using var mixer = new Mixer();

        mixer.ReorderUserMixes(["chat", "stream"]);

        Assert.Equal(["monitor", "chat", "stream", "auxout"], mixer.Config.Mixes.Select(m => m.Id));
        Assert.Equal(["chat", "stream"], mixer.ExportSettings().UserMixes!.Select(m => m.Id));
    }

    [Theory]
    [InlineData("game,music,browser,system,voicechat")]
    [InlineData("game,game,browser,system,voicechat,sfx")]
    [InlineData("game,music,browser,system,voicechat,sfx,unknown")]
    public void InvalidChannelOrdersAreRejectedWithoutChangingLayout(string order)
    {
        using var mixer = new Mixer();
        string[] before = [.. mixer.Config.Channels.Select(c => c.Id)];

        Assert.Throws<InvalidOperationException>(() =>
            mixer.ReorderUserChannels(order.Split(',')));

        Assert.Equal(before, mixer.Config.Channels.Select(c => c.Id));
    }

    [Fact]
    public void MonitoredMixIsValidatedAndPersisted()
    {
        using var mixer = new Mixer();

        mixer.SetMonitoredMix("stream");

        Assert.Equal("stream", mixer.MonitoredMixId);
        Assert.Equal("stream", mixer.ExportSettings().MonitoredMixId);
        Assert.Equal("stream", mixer.Snapshot().MonitoredMixId);
        Assert.Throws<InvalidOperationException>(() => mixer.SetMonitoredMix("deleted"));
        Assert.Equal("stream", mixer.MonitoredMixId);
    }
}
