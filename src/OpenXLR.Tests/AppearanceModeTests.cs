using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;
using Modes = OpenXLR.Core.AppearanceModes;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class AppearanceModeTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("openxlr-appearance-").FullName;
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public AppearanceModeTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Directory.Delete(_config, true);
    }

    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("unknown", "system")]
    [InlineData("LIGHT", "system")]
    [InlineData("light", "light")]
    [InlineData("dark", "dark")]
    [InlineData("system", "system")]
    public void LocalPreferencesUseKnownModesOrFollowTheDesktop(string? input, string expected)
    {
        Assert.Equal(expected, Modes.Normalize(input));
        Assert.Equal(expected, OpenXLR.UI.AppearanceModes.Normalize(input));
        Assert.Equal(expected, OpenXLR.Tui.AppearanceModes.Normalize(input));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"nested\":[true]}")]
    [InlineData("\"unknown\"")]
    public void MalformedLocalModeDoesNotDiscardOtherSettings(string mode)
    {
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(Path.Combine(UiSettings.ConfigDir, "ui.json"),
            "{\"startMinimized\":true,\"appearanceMode\":" + mode + ",\"skin\":\"deck\",\"minimizeToTray\":true}");
        UiSettings settings = UiSettings.Load();
        Assert.Equal(Modes.System, settings.AppearanceMode);
        Assert.True(settings.StartMinimized);
        Assert.True(settings.MinimizeToTray);
        Assert.Equal("deck", settings.Skin);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public void ModeRoundTripsThroughSettingsAndProfiles(string mode)
    {
        new UiSettings { Skin = "deck", AppearanceMode = mode, StartMinimized = true }.SaveChecked();
        var ui = UiSettings.Load();
        Assert.Equal(mode, ui.AppearanceMode);
        var json = JsonSerializer.Serialize(ui.ExportPresentation());
        var presentation = JsonSerializer.Deserialize<OpenXLR.Core.WindowPresentation>(json)!;
        presentation.Validate();
        ProfileStore.Save("test", "mode", new() { Presentation = presentation });
        Assert.Equal(mode, ProfileStore.Load("test", "mode")!.Presentation!.AppearanceMode);
        var recalled = new UiSettings { StartMinimized = true }.WithPresentation(
            JsonSerializer.Deserialize<OpenXLR.UI.WindowPresentation>(json)!, "revision");
        Assert.Equal(mode, recalled.AppearanceMode);
        Assert.Equal("deck", recalled.Skin);
        Assert.True(recalled.StartMinimized);
    }

    [Fact]
    public void LegacyProfilesPreserveTheCurrentModeAndNewProfilesWriteItExplicitly()
    {
        Assert.Equal(Modes.System, new UiSettings().AppearanceMode);
        var legacy = new OpenXLR.UI.WindowPresentation { Skin = "deck" };
        Assert.Null(legacy.AppearanceMode);
        var settings = new UiSettings { AppearanceMode = Modes.Light };
        Assert.Equal(Modes.Light, settings.WithPresentation(legacy, "legacy").AppearanceMode);
        Assert.Equal(Modes.Light, settings.ExportPresentation().AppearanceMode);
        Assert.Equal(Modes.System, (settings with { AppearanceMode = "broken" }).ExportPresentation().AppearanceMode);
    }

    [Theory]
    [InlineData("\"sepia\"")]
    [InlineData("\"LIGHT\"")]
    [InlineData("42")]
    [InlineData("[]")]
    public void InvalidProfileModeRejectsTheWholeProfile(string mode)
    {
        string path = Path.Combine(UiSettings.ConfigDir, "profiles", "test", "invalid.json");
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(path,
            "{\"device\":{\"gainDb\":75},\"presentation\":{\"appearanceMode\":" + mode + "}}");
        Assert.Throws<JsonException>(() => ProfileStore.Load("test", "invalid"));
    }

    [Fact]
    public void LightPaletteUsesTheExistingSkinContractWithoutAddingASkinChoice()
    {
        SkinEntry light = SkinService.MaterialLight;
        Assert.Empty(light.Errors);
        Assert.NotEmpty(light.Package.Tokens);
        Assert.All(light.Package.Tokens.Keys, token => Assert.NotNull(SkinTokens.Find(token)));
        Assert.Empty(light.Package.Controls);
        Assert.DoesNotContain(SkinCatalog.BuiltIn(), entry => entry.Id == "material-light");
        Assert.All(light.Package.Tokens.Values, value => Assert.IsType<SkinSolid>(value));
    }
}
