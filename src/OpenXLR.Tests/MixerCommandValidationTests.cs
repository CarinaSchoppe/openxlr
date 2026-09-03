using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MixerCommandValidationTests
{
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
