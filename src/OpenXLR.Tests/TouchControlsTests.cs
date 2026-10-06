using System.Text.Json;
using OpenXLR.UI;
using Presentation = OpenXLR.Core.WindowPresentation;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class TouchControlsTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"large\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void InvalidLocalSizingDoesNotDiscardOtherSettings(string value)
        => InConfig(() =>
        {
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(Path.Combine(UiSettings.ConfigDir, "ui.json"),
                "{\"touchControls\":" + value + ",\"compactMixer\":true,\"minimizeToTray\":true,\"skin\":\"deck\"}");
            UiSettings settings = UiSettings.Load();
            Assert.False(settings.TouchControls);
            Assert.True(settings.CompactMixer);
            Assert.True(settings.MinimizeToTray);
            Assert.Equal("deck", settings.Skin);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SizingRoundTripsAndLegacyProfilesPreserveIt(bool touch)
        => InConfig(() =>
        {
            new UiSettings { TouchControls = touch, MinimizeToTray = true }.SaveChecked();
            UiSettings settings = UiSettings.Load();
            Assert.Equal(touch, settings.TouchControls);
            Assert.Equal(touch, settings.ExportPresentation().TouchControls);
            Assert.Equal(touch, settings.WithPresentation(new(), "old").TouchControls);
            Assert.False(settings.WithPresentation(new() { TouchControls = false }, "standard").TouchControls);
            Assert.True(settings.WithPresentation(new() { TouchControls = true }, "touch").TouchControls);
            Assert.True(settings.WithPresentation(new() { TouchControls = !touch }, "new").MinimizeToTray);
            OpenXLR.Core.ProfileStore.Save("test", "sizing", new() { Presentation = new() { TouchControls = touch } });
            Assert.Equal(touch, OpenXLR.Core.ProfileStore.Load("test", "sizing")!.Presentation!.TouchControls);
        });

    [Theory]
    [InlineData("42")]
    [InlineData("\"touch\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void MalformedProfileSizingIsRejected(string value)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Presentation>(
            "{\"touchControls\":" + value + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SizingAndProfileChangesPreserveLocalLanguageAndFuturePreferences(bool touch)
        => InConfig(() =>
        {
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            OpenXLR.UI.OpenXlrPaths.WriteAtomic(path, """
                {"language":"ur","appearanceMode":"light","futureAppearance":{"density":"touch"},"futureEmpty":null}
                """);
            (UiSettings.Load() with { TouchControls = touch }).SaveChecked();
            var settings = UiSettings.Load();
            settings.WithPresentation(new() { TouchControls = !touch, Skin = "deck" }, "revision").SaveChecked();
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("ur", saved.RootElement.GetProperty("language").GetString());
            Assert.Equal("light", saved.RootElement.GetProperty("appearanceMode").GetString());
            Assert.Equal("touch", saved.RootElement.GetProperty("futureAppearance").GetProperty("density").GetString());
            Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("futureEmpty").ValueKind);
            Assert.Equal(!touch, saved.RootElement.GetProperty("touchControls").GetBoolean());
        });

    private static void InConfig(Action body)
    {
        string root = Directory.CreateTempSubdirectory("openxlr-touch-").FullName;
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root); body(); }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old); Directory.Delete(root, true); }
    }
}
