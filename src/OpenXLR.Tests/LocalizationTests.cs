using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

// The shared window ResourceManager also serves view-model tests. Keep its
// explicit multi-language probes separate from those tests, particularly
// while measuring allocations for an application's one selected language.
[Collection("xdg-config")]
public sealed class LocalizationTests
{
    public static IEnumerable<object[]> AllLanguages => Localizer.Languages.Select(c => new object[] { c.Id! });
    public static IEnumerable<object[]> TranslatedLanguages => AllLanguages.Where(c => (string)c[0] != "en");
    public static IEnumerable<object[]> LookupCases => Localizer.Languages.SelectMany(c =>
        new[] { "Active", "SkippedBundlesAfterFailedScan" }.Select(key => new object[] { c.Id!, key }));

    [Fact]
    public void ThePickerAndValidationUseTheSameUniqueShippedCatalogues()
    {
        string[] expected = ["en", "de", "es", "fr", "zh-Hans", "zh-Hant", "hi", "ar", "bn", "pt", "id", "ur", "ru", "ja", "pcm"];
        Assert.Equal(expected, Localizer.Languages.Select(c => c.Id));
        Assert.Equal(expected.Length, Localizer.Languages.Select(c => c.Id).Distinct().Count());
        Assert.All(Localizer.Languages, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Label));
            Assert.True(Localizer.IsSupported(c.Id!));
            Assert.Equal(c.Id, Localizer.Resolve(c.Id, CultureInfo.InvariantCulture));
        });
        Assert.False(Localizer.IsSupported("zh"));
        Assert.False(Localizer.IsSupported("../de"));
        string workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        var loop = Regex.Match(workflow, @"for language in ([^;]+); do");
        Assert.True(loop.Success);
        Assert.Equal(expected, loop.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData(null, "de-DE", "de")]
    [InlineData("system", "es-MX", "es")]
    [InlineData("", "fr-CA", "fr")]
    [InlineData(null, "it-IT", "en")]
    [InlineData("de-AT", "en-US", "de")]
    [InlineData("ES-mx", "en-US", "es")]
    [InlineData("fr", "en-US", "fr")]
    [InlineData("en-GB", "de-DE", "en")]
    [InlineData("../../de", "de-DE", "en")]
    [InlineData("unknown", "de-DE", "en")]
    [InlineData(null, "", "en")]
    [InlineData(null, "en-AU", "en")]
    [InlineData(null, "pt-BR", "pt")]
    [InlineData(null, "fr-BE", "fr")]
    [InlineData(null, "de-CH", "de")]
    [InlineData(null, "es-AR", "es")]
    [InlineData(null, "pt-PT", "pt")]
    [InlineData(null, "hi-IN", "hi")]
    [InlineData(null, "ar-EG", "ar")]
    [InlineData(null, "bn-BD", "bn")]
    [InlineData(null, "id-ID", "id")]
    [InlineData(null, "ur-PK", "ur")]
    [InlineData(null, "ru-RU", "ru")]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData(null, "pcm-NG", "pcm")]
    [InlineData(null, "zh-CN", "zh-Hans")]
    [InlineData(null, "zh-SG", "zh-Hans")]
    [InlineData(null, "zh-TW", "zh-Hant")]
    [InlineData(null, "zh-HK", "zh-Hant")]
    [InlineData(null, "zh-MO", "zh-Hant")]
    [InlineData("zh", "en-US", "zh-Hans")]
    [InlineData("ZH-hANT", "zh-CN", "zh-Hant")]
    [InlineData("zh-Hans-TW", "zh-TW", "zh-Hans")]
    [InlineData("zh-Hant-CN", "zh-CN", "zh-Hant")]
    [InlineData("pcm-NG", "de-DE", "pcm")]
    [InlineData(null, "zh-CHT", "zh-Hant")]
    [InlineData(null, "zh-CHS", "zh-Hans")]
    [InlineData("ZH-cht", "en-US", "zh-Hant")]
    [InlineData("zh-CHS", "zh-TW", "zh-Hans")]
    public void OnlyShippedLanguagesAreSelected(string? choice, string system, string expected)
        => Assert.Equal(expected, Localizer.Resolve(choice, CultureInfo.GetCultureInfo(system)));

    [Theory]
    [InlineData("en", "Close")]
    [InlineData("de", "Schließen")]
    [InlineData("es", "Cerrar")]
    [InlineData("fr", "Fermer")]
    [InlineData("zh-Hans", "关闭")]
    [InlineData("zh-Hant", "關閉")]
    [InlineData("hi", "बंद करें")]
    [InlineData("ar", "إغلاق")]
    [InlineData("bn", "বন্ধ করুন")]
    [InlineData("pt", "Fechar")]
    [InlineData("id", "Tutup")]
    [InlineData("ur", "بند کریں")]
    [InlineData("ru", "Закрыть")]
    [InlineData("ja", "閉じる")]
    [InlineData("pcm", "Close")]
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
        Assert.Equal("Close", Localizer.Get("Close", CultureInfo.GetCultureInfo("it-IT")));
        Assert.Equal("Schließen", Localizer.Get("Close", CultureInfo.GetCultureInfo("de-AT")));
        Assert.Same(numeric, CultureInfo.CurrentCulture);
        Assert.Same(ui, CultureInfo.CurrentUICulture);
        Assert.Throws<ArgumentException>(() => Localizer.Text("MissingResource"));
    }

    [Theory]
    [MemberData(nameof(TranslatedLanguages))]
    [InlineData("de-AT")]
    [InlineData("es-MX")]
    [InlineData("fr-CA")]
    public void AnUntranslatedEntryUsesTheExistingEnglishTextInsideATranslatedWindow(string language)
    {
        const string key = "SkippedBundlesAfterFailedScan";
        var culture = CultureInfo.GetCultureInfo(language);
        var translated = Localizer.Resources.GetResourceSet(Localizer.Languages.Any(c => c.Id == culture.Name)
            ? culture : culture.Parent, true, false);
        Assert.NotNull(translated);
        Assert.Null(translated.GetString(key));
        Assert.Equal(translated.GetString("Close"), Localizer.Get("Close", culture));
        Assert.Equal("Skipped after a failed scan: {0}", Localizer.Get(key, culture));
        Assert.Equal("Skipped after a failed scan: 2", string.Format(culture, Localizer.Get(key, culture), 2));
    }

    [Fact]
    public void ConcurrentLanguagesDoNotReuseAnotherCulturesTranslation()
    {
        Parallel.ForEach(new[] { ("en", "Close"), ("de", "Schließen"), ("es", "Cerrar"), ("fr", "Fermer") },
            entry =>
            {
                var culture = CultureInfo.GetCultureInfo(entry.Item1);
                for (int i = 0; i < 1000; i++)
                {
                    Assert.Equal(entry.Item2, Localizer.Get("Close", culture));
                    Assert.Equal("Skipped after a failed scan: {0}", Localizer.Get("SkippedBundlesAfterFailedScan", culture));
                }
            });
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void ProfileNamesAreArgumentsRatherThanTranslationOrFormatKeys(string language)
    {
        const string name = "Carina {0} <script> & canción 日本語";
        string message = string.Format(CultureInfo.GetCultureInfo(language),
            Localizer.Get("ProfileOverwriteMessage", CultureInfo.GetCultureInfo(language)), name);
        Assert.Contains(name, message);
        Assert.Single(Regex.Matches(message, Regex.Escape(name)));
    }

    [Theory]
    [MemberData(nameof(LookupCases))]
    public void RepeatedLookupsReuseTranslatedAndFallbackStringsWithoutAllocations(string language, string key)
    {
        long allocated = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                for (int i = 0; i < 1000; i++) _ = Localizer.Get(key, culture);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10_000; i++) _ = Localizer.Get(key, culture);
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
    public void MarkupTranslationsOnlyTargetTextOrContentProperties()
    {
        string folder = Path.Combine(Root, "src", "OpenXLR.UI");
        string[] properties = ["Text", "Content", "Title", "PlaceholderText", "ToolTip.Tip", "AutomationProperties.Name"];
        foreach (string file in Directory.EnumerateFiles(folder, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (var attribute in XDocument.Load(file).Descendants().Attributes()
                .Where(a => a.Value.StartsWith("{loc:Text ", StringComparison.Ordinal)))
                Assert.True(properties.Contains(attribute.Name.LocalName),
                    $"{file}: {attribute.Name} cannot consume localized text.");
    }

    [Fact]
    public void CataloguesPreserveEnglishKeysAndFormatArgumentsAndMissingTranslationsFallBack()
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
        foreach (string language in new[] { "" }.Concat(Localizer.Languages.Where(c => c.Id != "en").Select(c => "." + c.Id)))
        {
            var translated = Read(language);
            Assert.Empty(translated.Keys.Except(english.Keys));
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
            foreach (string key in english.Keys.Except(translated.Keys))
                Assert.Equal(english[key], Localizer.Get(key, CultureInfo.GetCultureInfo(language[1..])));
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
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("system", null)]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("es", "es")]
    [InlineData("fr", "fr")]
    [InlineData("de-AT", "de")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-CHT", "zh-Hant")]
    [InlineData("zh-CHS", "zh-Hans")]
    [InlineData("pcm-NG", "pcm")]
    [InlineData("ar-EG", "ar")]
    [InlineData("../../de", "en")]
    public async Task ThePickerReflectsSavedPreferencesAndRefusesUnknownChoices(string? saved, string? selected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-language-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            new UiSettings { Language = saved }.SaveChecked();
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            string original = File.ReadAllText(path);
            await using var client = new DaemonClient();
            var options = new OptionsViewModel(client, new MainViewModel(client));
            Assert.Equal(selected, options.SelectedLanguage!.Id);
            Assert.Equal(new[] { "English", "Deutsch", "Español", "Français" },
                options.LanguageChoices.Skip(1).Take(4).Select(c => c.Label));
            var choice = options.SelectedLanguage;
            options.SelectedLanguage = null;
            options.SelectedLanguage = new LanguageChoice("../de", "Unknown");
            Assert.Same(choice, options.SelectedLanguage);
            Assert.Null(options.LanguageError);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

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
            (UiSettings.Load() with { MinimizeToTray = true }).SaveChecked();
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
            initial.SaveChecked();
            foreach (string? language in Localizer.Languages.Select(c => c.Id).Append(null))
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
