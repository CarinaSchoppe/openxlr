using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class SavedMixerValidationTests
{
    [Theory]
    [InlineData("{\"mixVolumes\":null}")]
    [InlineData("{\"mixMuted\":null}")]
    [InlineData("{\"levels\":null}")]
    [InlineData("{\"channelMuted\":[null]}")]
    [InlineData("{\"monitorOutputs\":[null]}")]
    [InlineData("{\"monitorFeeds\":{\"output\":null}}")]
    [InlineData("{\"inserts\":{\"xlr1\":null}}")]
    [InlineData("{\"inserts\":{\"xlr1\":[null]}}")]
    [InlineData("{\"inserts\":{\"xlr1\":[{\"id\":null,\"kind\":\"lv2\",\"plugin\":\"test\"}]}}")]
    [InlineData("{\"inserts\":{\"xlr1\":[{\"id\":\"one\",\"kind\":\"lv2\",\"plugin\":\"test\",\"params\":null}]}}")]
    [InlineData("{\"mixVolumes\":{\"monitor\":1e999}}")]
    [InlineData("{\"inserts\":{\"xlr1\":[{\"id\":\"one\",\"kind\":\"lv2\",\"plugin\":\"test\",\"params\":{\"gain\":1e999}}]}}")]
    public void InvalidMixerDataIsRefusedByBothLoadersBeforeItCanBeApplied(string json)
    {
        InConfig(root =>
        {
            string settings = Path.Combine(root, "mixer.json");
            File.WriteAllText(settings, json);
            Assert.Null(MixerSettings.Load(settings));
            string profilePath = Path.Combine(root, "openxlr", "profiles", "0fd9-007d", "Test.json");
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            string profile = "{\"device\":{\"gainDb\":75},\"mixer\":" + json + "}";
            File.WriteAllText(profilePath, profile);
            Assert.Throws<JsonException>(() => ProfileStore.Load("0fd9:007d", "Test"));
            Assert.Equal(json, File.ReadAllText(settings));
            Assert.Equal(profile, File.ReadAllText(profilePath));
        });
    }

    [Theory]
    [InlineData("{\"inserts\":null}")]
    [InlineData("{\"monitorOutputs\":null}")]
    [InlineData("{\"monitorFeeds\":null}")]
    [InlineData("{\"appOverrides\":null}")]
    [InlineData("{\"appOverrides\":{\"app\":null}}")]
    [InlineData("{\"knownApps\":null}")]
    [InlineData("{\"knownApps\":[null]}")]
    [InlineData("{\"knownApps\":[{\"identity\":null,\"label\":\"App\",\"channelId\":\"system\"}]}")]
    public void SettingsRejectMissingRequiredCollectionsAndAppFields(string json)
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, json);
            Assert.Null(MixerSettings.Load(path));
        });
    }

    [Theory]
    [InlineData("{\"device\":{\"hpVolumeDb\":1e999}}")]
    [InlineData("{\"device\":{\"hp2VolumeDb\":-1e999}}")]
    [InlineData("{\"device\":{\"auxLevelDb\":1e999}}")]
    [InlineData("{\"mixer\":{\"outputVolume\":1e999}}")]
    public void SavedOutputLevelsCannotOverflow(string json)
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "openxlr", "profiles", "0fd9-007d", "Bad.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            Assert.Throws<JsonException>(() => ProfileStore.Load("0fd9:007d", "Bad"));
            Assert.Equal(json, File.ReadAllText(path));
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("device", out var device))
            {
                string directory = Path.Combine(root, "openxlr", "devices", "0fd9-007d");
                Directory.CreateDirectory(directory);
                foreach (string file in new[] { "last-state.json", "defaults.json" })
                    File.WriteAllText(Path.Combine(directory, file), device.GetRawText());
                Assert.Null(DeviceStateStore.LoadLast("0fd9:007d"));
                Assert.Null(DeviceStateStore.LoadDefaults("0fd9:007d"));
            }
        });
    }

    [Fact]
    public void OldProfilesKeepNullableRoutingAndMissingPluginsRemainLoadable()
    {
        InConfig(root =>
        {
            var missing = new InsertDefinition { Id = "one", Kind = "lv2", Plugin = "urn:not-installed" };
            ProfileStore.Save("0fd9:007d", "Old", new Profile { Mixer = new MixerScene { Inserts = new() { ["xlr1"] = [missing] } } });
            var scene = ProfileStore.Load("0fd9:007d", "Old")!.Mixer!;
            Assert.Null(scene.MonitorOutputs);
            Assert.Null(scene.MonitorFeeds);
            Assert.Equal(missing.Plugin, scene.Inserts!["xlr1"][0].Plugin);
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, "{}");
            Assert.NotNull(MixerSettings.Load(path));
        });
    }

    private static void InConfig(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-validation-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
        try { action(root); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
