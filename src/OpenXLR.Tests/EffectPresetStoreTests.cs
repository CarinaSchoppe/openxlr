using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class EffectPresetStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-presets-").FullName;

    [Fact]
    public void ChainCanSaveLoadDuplicateRenameExportAndDelete()
    {
        EffectChainPreset original = Chain("Voice", 0.25);
        EffectPresetStore.SaveChain(original, _root);
        EffectPresetStore.DuplicateChain("Voice", "Voice Copy", _root);
        EffectPresetStore.RenameChain("Voice Copy", "Broadcast", _root);

        EffectChainPreset restored = EffectPresetStore.LoadChain("Broadcast", _root)!;
        byte[] exported = EffectPresetStore.ExportChain("Broadcast", _root);

        Assert.Equal(0.25, restored.Inserts[0].Params["gain"]);
        Assert.Contains("Broadcast", Encoding.UTF8.GetString(exported));
        Assert.Equal(["Broadcast", "Voice"], EffectPresetStore.ListChains(_root));
        Assert.True(EffectPresetStore.DeleteChain("Broadcast", _root));
        Assert.False(EffectPresetStore.DeleteChain("Broadcast", _root));
    }

    [Fact]
    public void ImportRejectsTraversalMalformedOversizedAndUnknownSchema()
    {
        Assert.Throws<InvalidOperationException>(() => EffectPresetStore.NormalizeName("../escape"));
        Assert.Throws<InvalidOperationException>(() =>
            EffectPresetStore.ImportChain("{"u8.ToArray(), _root));
        Assert.Throws<InvalidOperationException>(() =>
            EffectPresetStore.ImportChain(new byte[EffectPresetStore.MaxDocumentBytes + 1], _root));
        Assert.Throws<InvalidOperationException>(() =>
            EffectPresetStore.ImportChain("""{"schemaVersion":99,"name":"Future","inserts":[]}"""u8.ToArray(), _root));
    }

    [Fact]
    public void CompleteSlotStateRoundTrips()
    {
        EffectChainPreset original = Chain("State", 0.75) with
        {
            Inserts = [Chain("State", 0.75).Inserts[0] with
            {
                State = Convert.ToBase64String([1, 2, 3, 4]),
                Sidechains = new() { ["sidechain"] = "channel:voicechat" },
            }],
        };

        EffectPresetStore.SaveChain(original, _root);
        EffectChainPreset restored = EffectPresetStore.LoadChain("State", _root)!;

        Assert.Equal(original.Name, restored.Name);
        InsertDefinition slot = Assert.Single(restored.Inserts);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), slot.State);
        Assert.Equal("channel:voicechat", slot.Sidechains["sidechain"]);
    }

    [Fact]
    public void PluginPresetSupportsFullCrudAndPortableImport()
    {
        InsertDefinition insert = Chain("Plugin", 0.4).Inserts[0];
        EffectPresetStore.SavePlugin(new PluginPreset { Name = "Gentle", Insert = insert }, _root);
        EffectPresetStore.DuplicatePlugin("Gentle", "Broadcast", _root);
        EffectPresetStore.RenamePlugin("Broadcast", "Aggressive", _root);

        byte[] exported = EffectPresetStore.ExportPlugin("Aggressive", _root);
        Assert.True(EffectPresetStore.DeletePlugin("Aggressive", _root));
        PluginPreset imported = EffectPresetStore.ImportPlugin(exported, _root);

        Assert.Equal("Aggressive", imported.Name);
        Assert.Equal(0.4, imported.Insert.Params["gain"]);
        Assert.Equal(["Aggressive", "Gentle"], EffectPresetStore.ListPlugins(_root));
    }

    [Fact]
    public void InvalidNativeStateAndNonFiniteParameterAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => EffectPresetStore.Validate(
            Chain("Bad", double.NaN)));
        Assert.Throws<InvalidOperationException>(() => EffectPresetStore.Validate(
            Chain("Bad", 1) with { Inserts = [Chain("Bad", 1).Inserts[0] with { State = "not-base64" }] }));
    }

    private static EffectChainPreset Chain(string name, double gain) => new()
    {
        Name = name,
        Inserts =
        [
            new InsertDefinition
            {
                Id = "compressor",
                Kind = "lv2",
                Plugin = "urn:test:compressor",
                Label = "Compressor",
                Params = new() { ["gain"] = gain },
            },
        ],
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
