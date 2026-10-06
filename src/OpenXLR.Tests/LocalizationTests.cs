using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData(null, "de-DE", "de")]
    [InlineData("system", "es-MX", "es")]
    [InlineData("", "fr-CA", "fr")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("de-AT", "en-US", "de")]
    [InlineData("ES-mx", "en-US", "es")]
    [InlineData("fr", "en-US", "fr")]
    [InlineData("en-GB", "de-DE", "en")]
    [InlineData("../../de", "de-DE", "en")]
    [InlineData("unknown", "de-DE", "en")]
    public void OnlyShippedLanguagesAreSelected(string? choice, string system, string expected)
        => Assert.Equal(expected, Localizer.Resolve(choice, CultureInfo.GetCultureInfo(system)));

    [Theory]
    [InlineData("en", "Close")]
    [InlineData("de", "Schließen")]
    [InlineData("es", "Cerrar")]
    [InlineData("fr", "Fermer")]
    public void CompiledCataloguesAreAvailableWithoutTheSourceTree(string language, string close)
    {
        Assert.Equal(close, Localizer.Get("Close", CultureInfo.GetCultureInfo(language)));
        if (language != "en")
            Assert.NotNull(typeof(Localizer).Assembly.GetSatelliteAssembly(CultureInfo.GetCultureInfo(language)));
    }

    [Fact]
    public void LookupFallsBackToEnglishAndDoesNotChangeNumericOrProtocolCulture()
    {
        var numeric = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        Assert.Equal("Close", Localizer.Get("Close", CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("Schließen", Localizer.Get("Close", CultureInfo.GetCultureInfo("de-AT")));
        Assert.Same(numeric, CultureInfo.CurrentCulture);
        Assert.Same(ui, CultureInfo.CurrentUICulture);
        Assert.Throws<ArgumentException>(() => Localizer.Text("MissingResource"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    public void ProfileNamesAreArgumentsRatherThanTranslationOrFormatKeys(string language)
    {
        const string name = "Carina {0} <script> & canción 日本語";
        string message = string.Format(CultureInfo.GetCultureInfo(language),
            Localizer.Get("ProfileOverwriteMessage", CultureInfo.GetCultureInfo(language)), name);
        Assert.Contains(name, message);
        Assert.Single(Regex.Matches(message, Regex.Escape(name)));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    public void RepeatedStatusLookupsReuseCachedStringsWithoutAllocations(string language)
    {
        long allocated = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                for (int i = 0; i < 1000; i++) _ = Localizer.Get("Active", culture);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10_000; i++) _ = Localizer.Get("Active", culture);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.Equal(0, allocated);
    }

    internal static string Root
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "roadmap.md")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    [Fact]
    public void EveryCatalogueHasTheSameKeysAndFormatArgumentsAndEveryUseIsDefined()
    {
        string folder = Path.Combine(Root, "src", "OpenXLR.UI", "Localization");
        Dictionary<string, string> Read(string suffix)
        {
            var data = XDocument.Load(Path.Combine(folder, $"Strings{suffix}.resx")).Root!.Elements("data").ToArray();
            Assert.Equal(data.Length, data.Select(d => d.Attribute("name")!.Value).Distinct().Count());
            return data.ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
        }
        var english = Read("");
        Assert.True(english.Count >= 250);
        foreach (string language in new[] { "", ".de", ".es", ".fr" })
        {
            var translated = Read(language);
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            foreach ((string key, string text) in translated)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                var format = CompositeFormat.Parse(text);
                Assert.Equal(CompositeFormat.Parse(english[key]).MinimumArgumentCount, format.MinimumArgumentCount);
                string Indices(string value) => string.Join(",", Regex.Matches(value, @"\{(\d+)(?:[,}:])")
                    .Select(m => m.Groups[1].Value).Order());
                Assert.Equal(Indices(english[key]), Indices(text));
                Assert.DoesNotContain('\u2014', text);
                // A catalogue may load while a missing satellite file falls
                // back to English. This checks the actual compiled value too.
                Assert.Equal(text, Localizer.Get(key, language == "" ? CultureInfo.InvariantCulture
                    : CultureInfo.GetCultureInfo(language[1..])));
            }
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Root, "src", "OpenXLR.UI"), "*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, "loc:Text Key=([A-Za-z0-9]+)|Localizer\\.(?:Text|Format)\\(\"([A-Za-z0-9]+)\""))
            {
                string key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                Assert.True(english.ContainsKey(key), $"{file}: missing text {key}");
                used.Add(key);
            }
        }
        Assert.Equal(english.Keys.Order(), used.Order());
    }
}

[Collection("xdg-config")]
public sealed class LanguageSettingsTests
{
    [Fact]
    public void AnExplicitNullCollapsedListDoesNotBreakWindowRestoration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-language-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            OpenXLR.UI.OpenXlrPaths.WriteAtomic(path,
                """{"skin":"material","language":"de","collapsedSections":null}""");
            var loaded = UiSettings.Load();
            // This is the input to MainWindow.RestoreSectionState, which
            // must receive an enumerable even after a hand-edited file.
            Assert.Empty(new HashSet<string>(loaded.CollapsedSections, StringComparer.Ordinal));
            OptionsViewModel.SaveLanguage("fr");
            Assert.Empty(UiSettings.Load().CollapsedSections);
            Assert.Equal("material", UiSettings.Load().Skin);
            Assert.Equal("fr", UiSettings.Load().Language);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LanguageSaveRetainsFieldsOwnedByOtherOrNewerWindowFeatures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-language-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            const string original = """
                {"skin":"material","language":"en","startMinimized":true,
                 "sectionOrder":["MonitorTile","InputsTile"],"futureAppearance":{"density":"touch","scale":1.5},
                 "futureFlag":false,"futureEmpty":null}
                """;
            OpenXLR.UI.OpenXlrPaths.WriteAtomic(path, original);
            OptionsViewModel.SaveLanguage("de");
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("de", saved.RootElement.GetProperty("language").GetString());
            Assert.Equal("material", saved.RootElement.GetProperty("skin").GetString());
            Assert.True(saved.RootElement.GetProperty("startMinimized").GetBoolean());
            Assert.Equal("MonitorTile", saved.RootElement.GetProperty("sectionOrder")[0].GetString());
            Assert.Equal("touch", saved.RootElement.GetProperty("futureAppearance").GetProperty("density").GetString());
            Assert.Equal(1.5, saved.RootElement.GetProperty("futureAppearance").GetProperty("scale").GetDouble());
            Assert.False(saved.RootElement.GetProperty("futureFlag").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, saved.RootElement.GetProperty("futureEmpty").ValueKind);
            // Other window writes must retain the saved language and those
            // same fields, rather than undo the preservation on the next save.
            (UiSettings.Load() with { MinimizeToTray = true }).SaveRequired();
            using var later = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("de", later.RootElement.GetProperty("language").GetString());
            Assert.True(later.RootElement.GetProperty("minimizeToTray").GetBoolean());
            Assert.Equal(saved.RootElement.GetProperty("futureAppearance").GetRawText(),
                later.RootElement.GetProperty("futureAppearance").GetRawText());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LanguageSavePreservesOtherPreferencesAndReportsARefusedWrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-language-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            var initial = new UiSettings { Skin = "material", StartMinimized = true,
                CheckForUpdates = true, CollapsedSections = ["InputsTile"] };
            initial.SaveRequired();
            foreach (string? language in new string?[] { "de", "es", "fr", "en", null })
            {
                OptionsViewModel.SaveLanguage(language);
                var loaded = UiSettings.Load();
                Assert.Equal(language, loaded.Language);
                Assert.Equal(initial.Skin, loaded.Skin);
                Assert.Equal(initial.StartMinimized, loaded.StartMinimized);
                Assert.Equal(initial.CheckForUpdates, loaded.CheckForUpdates);
                Assert.Equal(initial.CollapsedSections, loaded.CollapsedSections);
            }
            Assert.Throws<ArgumentException>(() => OptionsViewModel.SaveLanguage("../de"));
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            foreach (string invalid in new[] { "{\"skin\":\"material\",\"broken\":", "null", "[]", "true", "\"de\"" })
            {
                OpenXLR.UI.OpenXlrPaths.WriteAtomic(path, invalid);
                Assert.Throws<System.Text.Json.JsonException>(() => OptionsViewModel.SaveLanguage("de"));
                Assert.Equal(invalid, File.ReadAllText(path));
            }
            File.Delete(path);
            Directory.CreateDirectory(path);
            Assert.ThrowsAny<IOException>(() => OptionsViewModel.SaveLanguage("de"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
