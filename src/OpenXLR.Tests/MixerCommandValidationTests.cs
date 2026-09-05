using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MixerCommandValidationTests
{
    [RequiresLspFact]
    public void InsertValidationCannotReplaceAGoodChainWithInvalidControls()
    {
        // Production publishes the isolated scanner's result. This unit test
        // exercises validation directly and therefore supplies the same
        // installed catalogue explicitly.
        PluginRegistry.Replace(Lv2Catalog.Plugins);
        using var mixer = new Mixer();
        var insert = new InsertDefinition
        {
            Id = "compressor",
            Kind = "lv2",
            Plugin = "http://lsp-plug.in/plugins/lv2/compressor_stereo",
            Params = new() { ["g_out"] = 0.5 }
        };
        mixer.SetInserts("game", [insert]);
        Assert.Throws<InvalidOperationException>(() => mixer.SetInserts("xlr1", [insert with { Plugin = "test:missing" }]));
        Assert.Throws<InvalidOperationException>(() => mixer.SetInserts("game", [insert with { Params = new() { ["g_out"] = double.NaN } }]));
        Assert.Throws<InvalidOperationException>(() => mixer.SetInserts("game", [insert with { Params = new() { ["unknown"] = 1 } }]));
        Assert.Equal(0.5, mixer.ExportSettings().Inserts["game"][0].Params["g_out"]);
    }

    [Fact]
    public void EveryChannelHasAnIndependentPersistedInsertTarget()
    {
        using var mixer = new Mixer();
        foreach (var channel in mixer.Config.Channels) mixer.SetInserts(channel.Id, []);
        Assert.Equal(mixer.Config.Channels.Count, mixer.ExportSettings().Inserts.Count);
        Assert.NotEqual(mixer.Config.Channels.Single(c => c.Id == "game").SinkName,
            mixer.Config.Channels.Single(c => c.Id == "game").FanOutSinkName);
        Assert.Equal(mixer.Config.Channels.Single(c => c.Id == "xlr1").SinkName,
            mixer.Config.Channels.Single(c => c.Id == "xlr1").FanOutSinkName);
    }

    [Fact]
    public void NativeUiRequiresALiveInstance()
    {
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.ShowInsertUi("game", "missing"));
        Assert.False(mixer.SyncPluginControls());
    }
    [Fact]
    public void StaleMixCommandsCannotCreateGhostSettings()
    {
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.SetMixVolume("deleted", 0.5));
        Assert.Throws<InvalidOperationException>(() => mixer.SetMixMuted("deleted", true));
        Assert.DoesNotContain("deleted", mixer.ExportSettings().MixVolumes.Keys);
        Assert.DoesNotContain("deleted", mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void UnknownInsertTargetsAndDuplicateIdsAreRejected()
    {
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.SetInserts("deleted", []));
        var insert = new InsertDefinition { Id = "same", Kind = "lv2", Plugin = "test:plugin" };
        Assert.Throws<InvalidOperationException>(() => mixer.SetInserts("xlr1", [insert, insert]));
        Assert.Empty(mixer.ExportSettings().Inserts);
    }
}
