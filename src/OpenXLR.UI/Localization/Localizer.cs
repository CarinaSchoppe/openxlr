using System;
using System.Globalization;
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
    internal static readonly ResourceManager Resources = new("OpenXLR.UI.Localization.Strings", typeof(Localizer).Assembly);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("en");
    public static string Language => _culture.Name;

    internal static string Resolve(string? language, CultureInfo systemCulture)
    {
        if (string.IsNullOrEmpty(language) || language == "system")
            language = systemCulture.TwoLetterISOLanguageName;
        // Only shipped catalogues are selected. No arbitrary locale, file or
        // assembly name comes from the preference or environment variable.
        string primary = language.Split('-')[0].ToLowerInvariant();
        return primary is "en" or "de" or "es" or "fr" ? primary : "en";
    }

    internal static void Initialize()
    {
        string? requested = Environment.GetEnvironmentVariable("OPENXLR_LANGUAGE");
        if (string.IsNullOrEmpty(requested)) requested = UiSettings.Load().Language;
        _culture = CultureInfo.GetCultureInfo(Resolve(requested, CultureInfo.CurrentUICulture));
    }

    public static string Text(string key) => Get(key, _culture);

    internal static string Get(string key, CultureInfo culture) =>
        Resources.GetString(key, culture) ?? throw new ArgumentException($"Unknown window text: {key}", nameof(key));

    public static string Format(string key, params object?[] arguments) =>
        string.Format(_culture, Text(key), arguments);
}

/// <summary>Static markup text, using the language selected before any window is built.</summary>
public sealed class TextExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Text(Key);
}
