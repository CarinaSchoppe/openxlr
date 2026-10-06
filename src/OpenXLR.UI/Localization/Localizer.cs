using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace OpenXLR.UI.Localization;

/// <summary>
/// Window text only. The selected culture never changes the process culture,
/// protocol values, saved names or plugin-provided text. It stays fixed for
/// the window's lifetime so an open editor need not be rebuilt.
/// </summary>
public static class Localizer
{
    // One list drives locale selection, the settings picker and validation.
    // Native names let users recover from a language they cannot read.
    internal static IReadOnlyList<LanguageChoice> Languages { get; } = Array.AsReadOnly<LanguageChoice>(
    [
        new("en", "English"), new("de", "Deutsch"), new("es", "Español"), new("fr", "Français"),
        new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"), new("hi", "हिन्दी"), new("ar", "العربية"),
        new("bn", "বাংলা"), new("pt", "Português"), new("id", "Bahasa Indonesia"), new("ur", "اردو"),
        new("ru", "Русский"), new("ja", "日本語"), new("pcm", "Naijíriá Píjin"),
    ]);

    internal static bool IsSupported(string language) => Languages.Any(choice => choice.Id == language);

    internal static readonly ResourceManager Resources = new("OpenXLR.UI.Localization.Strings", typeof(Localizer).Assembly);
    private static readonly ConcurrentDictionary<(string Key, CultureInfo Culture), string> Texts = new();
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("en");
    public static string Language => _culture.Name;
    public static bool Overridden { get; private set; }

    internal static string Resolve(string? language, CultureInfo systemCulture)
    {
        if (string.IsNullOrEmpty(language) || language == "system")
            language = systemCulture.Name;
        // Only shipped catalogues are selected. No arbitrary locale, file or
        // assembly name comes from the preference or environment variable.
        string[] parts = language.ToLowerInvariant().Split('-');
        string primary = parts[0];
        if (primary == "zh")
        {
            // Explicit script wins over region. Bare zh uses simplified;
            // Taiwan, Hong Kong and Macao use traditional unless specified.
            if (parts.Contains("hant")) return "zh-Hant";
            if (parts.Contains("hans")) return "zh-Hans";
            return parts.Skip(1).Any(part => part is "tw" or "hk" or "mo") ? "zh-Hant" : "zh-Hans";
        }
        return IsSupported(primary) ? primary : "en";
    }

    internal static void Initialize()
    {
        string? requested = Environment.GetEnvironmentVariable("OPENXLR_LANGUAGE");
        Overridden = !string.IsNullOrEmpty(requested);
        if (string.IsNullOrEmpty(requested)) requested = UiSettings.Load().Language;
        _culture = CultureInfo.GetCultureInfo(Resolve(requested, CultureInfo.CurrentUICulture));
    }

    public static string Text(string key) => Get(key, _culture);

    // ResourceManager falls back per key as well as per catalogue, but does
    // not cache a missing satellite entry's resolved string. Cache our fixed
    // window keys by culture so English fallbacks allocate only on first use.
    internal static string Get(string key, CultureInfo culture) =>
        Texts.GetOrAdd((key, culture), static entry => Resources.GetString(entry.Key, entry.Culture)
            ?? throw new ArgumentException($"Unknown window text: {entry.Key}", nameof(key)));

    public static string Format(string key, params object?[] arguments) =>
        string.Format(_culture, Text(key), arguments);
}

/// <summary>Static markup text, using the language selected before any window is built.</summary>
public sealed class TextExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Text(Key);
}
