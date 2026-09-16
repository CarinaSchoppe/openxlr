using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Validate saved data before it can partially change a live mixer or device.
/// JSON can contain null despite a non-nullable C# property, and a number such
/// as 1e999 can deserialize to infinity. Missing legacy fields keep their defaults;
/// plugin availability and layout migration remain the existing loaders' job.
/// </summary>
internal static class SavedMixerValidation
{
    internal static void Validate(MixerSettings settings)
    {
        Levels(settings.MixVolumes, "mixVolumes");
        Levels(settings.Levels, "levels");
        Names(settings.MixMuted, "mixMuted");
        Names(settings.ChannelMuted, "channelMuted");
        Names(settings.MonitorOutputs, "monitorOutputs");
        Mapping(settings.MonitorFeeds, "monitorFeeds");
        Mapping(settings.AppOverrides, "appOverrides");
        Inserts(settings.Inserts);
        Require(settings.KnownApps is not null && settings.KnownApps.All(app =>
            app is not null && app.Identity is not null && app.Label is not null && app.ChannelId is not null), "knownApps");
        // UserChannels/UserMixes deliberately tolerate malformed entries:
        // MixerConfig.FromSettings filters them and retains safe routing.
    }

    internal static void Validate(MixerScene scene)
    {
        Levels(scene.MixVolumes, "mixVolumes");
        Levels(scene.Levels, "levels");
        Names(scene.MixMuted, "mixMuted");
        Names(scene.ChannelMuted, "channelMuted");
        if (scene.MonitorOutputs is not null) Names(scene.MonitorOutputs, "monitorOutputs");
        if (scene.MonitorFeeds is not null) Mapping(scene.MonitorFeeds, "monitorFeeds");
        if (scene.Inserts is not null) Inserts(scene.Inserts);
        Require(scene.OutputVolume is null || double.IsFinite(scene.OutputVolume.Value), "outputVolume");
    }

    private static void Levels(IReadOnlyDictionary<string, double>? levels, string field)
        => Require(levels is not null && levels.Values.All(double.IsFinite), field);

    private static void Names(IReadOnlyCollection<string>? names, string field)
        => Require(names is not null && names.All(name => name is not null), field);

    private static void Mapping(IReadOnlyDictionary<string, string>? entries, string field)
        => Require(entries is not null && entries.Values.All(value => value is not null), field);

    private static void Inserts(IReadOnlyDictionary<string, List<InsertDefinition>>? chains)
    {
        Require(chains is not null, "inserts");
        foreach (var chain in chains)
        {
            Require(chain.Value is not null, "inserts");
            foreach (InsertDefinition insert in chain.Value)
            {
                Require(insert is not null && insert.Id is not null && insert.Kind is not null && insert.Plugin is not null, "inserts");
                Levels(insert.Params, "inserts.params");
            }
        }
    }

    private static void Require([DoesNotReturnIf(false)] bool valid, string field)
    {
        if (!valid) throw new JsonException($"Invalid saved mixer field '{field}': null entries and non-finite numbers are not supported.");
    }
}
