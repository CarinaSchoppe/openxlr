using System;
using System.Collections.Generic;
using System.Linq;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// One icon of the built-in set: a name a layout stores, a text character for
/// clients that draw text only, and SVG path data in a 24 by 24 box, filled
/// with the current colour under SVG's default nonzero rule.
/// </summary>
public sealed record LayoutIconDefinition(string Name, string Text, string Path);

/// <summary>
/// The icon an item wears before the user picks one. Decided here once, so
/// the window, the terminal mixer and the deck agree without three copies.
/// </summary>
public static class LayoutDefaultIcons
{
    /// <summary>
    /// A hardware input, a capture input, a shipped application channel, or
    /// any other application channel. A microphone strip wears the XLR
    /// microphone on a real XLR jack, the handheld microphone on a USB
    /// microphone's capsule or another source, and the headset on a
    /// Bluetooth headset's source.
    /// </summary>
    /// <param name="boundSource">The source a hardware strip is bound to instead of the interface, or null.</param>
    /// <param name="xlrJack">False while the interface's input is a capsule rather than an XLR jack.</param>
    public static string ForChannel(string id, bool hardware, string? captureSource, string? boundSource = null, bool xlrJack = true,
        int? captureMonoChannel = null) =>
        hardware ? (id is "aux" or "line" ? "jack"
            : boundSource is not null ? (boundSource.StartsWith("bluez_input.", StringComparison.Ordinal) ? "headset" : "microphone")
            : xlrJack ? "mic" : "microphone")
        : captureSource is not null ? (captureSource.StartsWith("bluez_input.", StringComparison.Ordinal) ? "headset"
            : captureMonoChannel is not null ? "microphone" : "camera")
        : id switch
        {
            "game" => "game",
            "music" => "music",
            "browser" => "browser",
            "system" => "cog",
            "voicechat" => "chat",
            "sfx" => "burst",
            _ => "apps",
        };

    /// <param name="kind">The state's mix kind: <c>monitor</c>, <c>virtualMic</c> or <c>auxPort</c>.</param>
    public static string ForMix(string id, string kind) =>
        kind == "monitor" ? "headset"
        : kind == "auxPort" ? "jack"
        : id switch
        {
            "stream" => "broadcast",
            "chat" => "people",
            _ => "podcast",
        };
}

/// <summary>
/// The icons a channel or mix can wear, shared as a linked source by the
/// daemon, the window and the terminal mixer so all three read one table.
/// The deck plugin gets the same table from <c>tools/deck-icons.py</c>.
/// </summary>
/// <remarks>
/// A stored icon is empty (the default for the item's kind), <see cref="None"/>,
/// one of the legacy <see cref="Glyphs"/>, or a name. A name is checked for
/// its shape only, so a layout may name an icon a skin defines; a client that
/// does not know a name draws the default instead.
/// </remarks>
public static class LayoutIcons
{
    /// <summary>The stored value for "no icon at all".</summary>
    public const string None = "none";

    /// <summary>The longest icon name.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The text glyphs layouts stored before the icon set existed. Still valid and drawn as text.</summary>
    public static IReadOnlyList<string> Glyphs { get; } = ["●", "♪", "♫", "✦", "◆", "▶", "◉"];

    /// <summary>The built-in set, in the order a picker offers it.</summary>
    public static IReadOnlyList<LayoutIconDefinition> BuiltIn { get; } =
    [
        new("mic", "●", "M12 2h0a3 3 0 0 1 3 3v5a3 3 0 0 1 -3 3h0a3 3 0 0 1 -3 -3v-5a3 3 0 0 1 3 -3z M19 10A7 7 0 0 1 5 10L7 10A5 5 0 0 0 17 10z M11 16.5h2v3.5h-2z M8 20h8a1 1 0 0 1 1 1v0a1 1 0 0 1 -1 1h-8a1 1 0 0 1 -1 -1v0a1 1 0 0 1 1 -1z"),
        new("microphone", "●", "M10.5 7a4.5 4.5 0 1 1 9 0a4.5 4.5 0 1 1 -9 0z M12.2 7a2.8 2.8 0 1 0 5.6 0a2.8 2.8 0 1 0 -5.6 0z M13.27 11.56L11.33 9.84L3.53 18.64L5.47 20.36z"),
        new("headset", "◉", "M3 13A9 9 0 0 1 21 13L19 13A7 7 0 0 0 5 13z M4 12h2a2 2 0 0 1 2 2v5a2 2 0 0 1 -2 2h-2a2 2 0 0 1 -2 -2v-5a2 2 0 0 1 2 -2z M18 12h2a2 2 0 0 1 2 2v5a2 2 0 0 1 -2 2h-2a2 2 0 0 1 -2 -2v-5a2 2 0 0 1 2 -2z"),
        new("speaker", "▶", "M2 9L6 9L11 4L11 20L6 15L2 15z M14.21 8.17A5 5 0 0 1 14.21 15.83L13.06 14.45A3.2 3.2 0 0 0 13.06 9.55z M17.11 4.72A9.5 9.5 0 0 1 17.11 19.28L15.95 17.9A7.7 7.7 0 0 0 15.95 6.1z"),
        new("jack", "◎", "M2.5 12a9.5 9.5 0 1 1 19 0a9.5 9.5 0 1 1 -19 0zM5 12a7 7 0 1 0 14 0a7 7 0 1 0 -14 0z M8.5 12a3.5 3.5 0 1 1 7 0a3.5 3.5 0 1 1 -7 0z"),
        new("music", "♫", "M3.5 18a3 3 0 1 1 6 0a3 3 0 1 1 -6 0z M14.5 16a3 3 0 1 1 6 0a3 3 0 1 1 -6 0z M8 6h2v12h-2z M19 4h2v12h-2z M8 6L21 3L21 6L8 9z"),
        new("game", "◆", "M7 7h10a5.5 5.5 0 0 1 5.5 5.5v0a5.5 5.5 0 0 1 -5.5 5.5h-10a5.5 5.5 0 0 1 -5.5 -5.5v0a5.5 5.5 0 0 1 5.5 -5.5z M6.5 11.5L5 11.5L5 13.5L6.5 13.5L6.5 15L8.5 15L8.5 13.5L10 13.5L10 11.5L8.5 11.5L8.5 10L6.5 10z M14.2 11a1.3 1.3 0 1 0 2.6 0a1.3 1.3 0 1 0 -2.6 0z M16.7 13.5a1.3 1.3 0 1 0 2.6 0a1.3 1.3 0 1 0 -2.6 0z"),
        new("browser", "○", "M5 3h14a3 3 0 0 1 3 3v12a3 3 0 0 1 -3 3h-14a3 3 0 0 1 -3 -3v-12a3 3 0 0 1 3 -3z M4 8v11h16v-11z M4.5 5.5a1 1 0 1 0 2 0a1 1 0 1 0 -2 0z M7.5 5.5a1 1 0 1 0 2 0a1 1 0 1 0 -2 0z"),
        new("chat", "◇", "M5 3h14a3 3 0 0 1 3 3v8a3 3 0 0 1 -3 3h-8l-5 4v-4h-1a3 3 0 0 1 -3 -3v-8a3 3 0 0 1 3 -3z M6.1 10a1.4 1.4 0 1 0 2.8 0a1.4 1.4 0 1 0 -2.8 0z M10.6 10a1.4 1.4 0 1 0 2.8 0a1.4 1.4 0 1 0 -2.8 0z M15.1 10a1.4 1.4 0 1 0 2.8 0a1.4 1.4 0 1 0 -2.8 0z"),
        new("people", "◐", "M5 7.5a3.5 3.5 0 1 1 7 0a3.5 3.5 0 1 1 -7 0z M1.5 20a7 7 0 0 1 14 0z M13.5 8.5a3 3 0 1 1 6 0a3 3 0 1 1 -6 0z M15.8 13.6a6 6 0 0 1 6.7 6.4h-5.4a8.6 8.6 0 0 0 -1.3 -6.4z"),
        new("broadcast", "◉", "M9.5 12a2.5 2.5 0 1 1 5 0a2.5 2.5 0 1 1 -5 0z M16.6 7.4A6.5 6.5 0 0 1 16.6 16.6L15.32 15.32A4.7 4.7 0 0 0 15.32 8.68z M7.4 16.6A6.5 6.5 0 0 1 7.4 7.4L8.68 8.68A4.7 4.7 0 0 0 8.68 15.32z M19.42 4.58A10.5 10.5 0 0 1 19.42 19.42L18.15 18.15A8.7 8.7 0 0 0 18.15 5.85z M4.58 19.42A10.5 10.5 0 0 1 4.58 4.58L5.85 5.85A8.7 8.7 0 0 0 5.85 18.15z"),
        new("camera", "▣", "M4.5 6h9a2.5 2.5 0 0 1 2.5 2.5v7a2.5 2.5 0 0 1 -2.5 2.5h-9a2.5 2.5 0 0 1 -2.5 -2.5v-7a2.5 2.5 0 0 1 2.5 -2.5z M17 10.5L22 7L22 17L17 13.5z"),
        new("bell", "▲", "M12 2.5a1.5 1.5 0 0 1 1.5 1.5v.7a6 6 0 0 1 4.5 5.8v4.5l2 2.5v1h-16v-1l2 -2.5v-4.5a6 6 0 0 1 4.5 -5.8v-.7a1.5 1.5 0 0 1 1.5 -1.5z M9.5 19.5h5a2.5 2.5 0 0 1 -5 0z"),
        new("star", "✦", "M12 2.6L14.47 9.2L21.51 9.51L15.99 13.9L17.88 20.69L12 16.8L6.12 20.69L8.01 13.9L2.49 9.51L9.53 9.2z"),
        new("globe", "○", "M2 12a10 10 0 1 1 20 0a10 10 0 1 1 -20 0zM3.7 12a8.3 8.3 0 1 0 16.6 0a8.3 8.3 0 1 0 -16.6 0z M12 2.5a4.6 9.5 0 0 1 0 19a4.6 9.5 0 0 1 0 -19z M12 4.2a2.9 7.8 0 0 0 0 15.6a2.9 7.8 0 0 0 0 -15.6z M2.5 11.2h19v1.6h-19z"),
        new("cog", "◎", "M19.37 10.16L22.27 10.37L22.27 13.63L19.37 13.84L18.51 15.91L20.41 18.11L18.11 20.41L15.91 18.51L13.84 19.37L13.63 22.27L10.37 22.27L10.16 19.37L8.09 18.51L5.89 20.41L3.59 18.11L5.49 15.91L4.63 13.84L1.73 13.63L1.73 10.37L4.63 10.16L5.49 8.09L3.59 5.89L5.89 3.59L8.09 5.49L10.16 4.63L10.37 1.73L13.63 1.73L13.84 4.63L15.91 5.49L18.11 3.59L20.41 5.89L18.51 8.09zM8.8 12a3.2 3.2 0 1 0 6.4 0a3.2 3.2 0 1 0 -6.4 0z"),
        new("film", "▣", "M4 3h16a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2z M6 3v18h12v-18z M3 5v2h2v-2z M3 9v2h2v-2z M3 13v2h2v-2z M3 17v2h2v-2z M19 5v2h2v-2z M19 9v2h2v-2z M19 13v2h2v-2z M19 17v2h2v-2z"),
        new("voice", "♪", "M3 10h0a1 1 0 0 1 1 1v2a1 1 0 0 1 -1 1h0a1 1 0 0 1 -1 -1v-2a1 1 0 0 1 1 -1z M7 7h0a1 1 0 0 1 1 1v8a1 1 0 0 1 -1 1h0a1 1 0 0 1 -1 -1v-8a1 1 0 0 1 1 -1z M11 3h0a1 1 0 0 1 1 1v16a1 1 0 0 1 -1 1h0a1 1 0 0 1 -1 -1v-16a1 1 0 0 1 1 -1z M15 6h0a1 1 0 0 1 1 1v10a1 1 0 0 1 -1 1h0a1 1 0 0 1 -1 -1v-10a1 1 0 0 1 1 -1z M19 9h0a1 1 0 0 1 1 1v4a1 1 0 0 1 -1 1h0a1 1 0 0 1 -1 -1v-4a1 1 0 0 1 1 -1z"),
        new("podcast", "●", "M12 2h0a3 3 0 0 1 3 3v4a3 3 0 0 1 -3 3h0a3 3 0 0 1 -3 -3v-4a3 3 0 0 1 3 -3z M19 9A7 7 0 0 1 5 9L7 9A5 5 0 0 0 17 9z M11 15.5h2v6.5h-2z M3.34 14A10 10 0 1 1 20.66 14L19.27 13.2A8.4 8.4 0 1 0 4.73 13.2z"),
        new("record", "●", "M2 12a10 10 0 1 1 20 0a10 10 0 1 1 -20 0zM3.8 12a8.2 8.2 0 1 0 16.4 0a8.2 8.2 0 1 0 -16.4 0z M6.5 12a5.5 5.5 0 1 1 11 0a5.5 5.5 0 1 1 -11 0z"),
        new("apps", "■", "M5 3h3.5a2 2 0 0 1 2 2v3.5a2 2 0 0 1 -2 2h-3.5a2 2 0 0 1 -2 -2v-3.5a2 2 0 0 1 2 -2z M15.5 3h3.5a2 2 0 0 1 2 2v3.5a2 2 0 0 1 -2 2h-3.5a2 2 0 0 1 -2 -2v-3.5a2 2 0 0 1 2 -2z M5 13.5h3.5a2 2 0 0 1 2 2v3.5a2 2 0 0 1 -2 2h-3.5a2 2 0 0 1 -2 -2v-3.5a2 2 0 0 1 2 -2z M15.5 13.5h3.5a2 2 0 0 1 2 2v3.5a2 2 0 0 1 -2 2h-3.5a2 2 0 0 1 -2 -2v-3.5a2 2 0 0 1 2 -2z"),
        new("burst", "✦", "M12 1.5L13.72 7.84L19.42 4.58L16.16 10.28L22.5 12L16.16 13.72L19.42 19.42L13.72 16.16L12 22.5L10.28 16.16L4.58 19.42L7.84 13.72L1.5 12L7.84 10.28L4.58 4.58L10.28 7.84z"),
        new("monitor", "▣", "M4 3h16a2 2 0 0 1 2 2v9a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2v-9a2 2 0 0 1 2 -2z M4 5v9h16v-9z M10.5 16h3v3h-3z M8 19h8a1 1 0 0 1 1 1v0a1 1 0 0 1 -1 1h-8a1 1 0 0 1 -1 -1v0a1 1 0 0 1 1 -1z"),
        new("dollar", "$", "M16.33 5.88A5 4.75 0 1 0 12 13L12 11A3 2.75 0 1 1 14.6 6.88z M12 11A5 4.75 0 1 1 7.67 18.12L9.4 17.12A3 2.75 0 1 0 12 13z M11 1.5h2v2h-2z M11 20.5h2v2h-2z"),
    ];

    /// <summary>The built-in icon of that name, or null.</summary>
    public static LayoutIconDefinition? Find(string? name)
    {
        foreach (LayoutIconDefinition icon in BuiltIn)
            if (icon.Name == name) return icon;
        return null;
    }

    public static bool IsGlyph(string? value) => value is not null && Glyphs.Contains(value);

    /// <summary>A lower-case ASCII letter, then up to 31 lower-case letters, digits or hyphens.</summary>
    public static bool IsName(string? value) =>
        value is { Length: > 0 and <= MaxNameLength } && char.IsAsciiLetterLower(value[0]) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>What a layout may store: empty, a glyph, or a name (<see cref="None"/> is a name).</summary>
    public static bool IsValidChoice(string? value) => value == "" || IsGlyph(value) || IsName(value);

    /// <summary>
    /// What a client draws for a stored icon: a glyph as it is, a name the
    /// client knows, nothing for <see cref="None"/>, and otherwise the
    /// default when the client knows that. Empty means draw nothing.
    /// </summary>
    /// <param name="knows">Whether this client can draw a name; the built-in set when null.</param>
    public static string Resolve(string? icon, string? defaultIcon, Func<string, bool>? knows = null)
    {
        knows ??= name => Find(name) is not null;
        if (icon == None) return "";
        if (IsGlyph(icon)) return icon!;
        if (IsName(icon) && knows(icon!)) return icon!;
        return IsName(defaultIcon) && knows(defaultIcon!) ? defaultIcon! : "";
    }

    /// <summary>The text a text-only client draws for a stored icon: the glyph, the icon's character, or nothing.</summary>
    public static string TextFor(string? icon, string? defaultIcon)
    {
        string shown = Resolve(icon, defaultIcon);
        return IsGlyph(shown) ? shown : Find(shown)?.Text ?? "";
    }

    /// <summary>
    /// Whether <paramref name="data"/> is SVG path data this format allows:
    /// absolute or relative M, L, H, V, C, S, Q, T, A and Z commands with
    /// plain numbers, starting with a move. Nothing else, so a value cannot
    /// carry markup, a fill rule or a reference to anything outside it.
    /// </summary>
    public static bool IsValidPath(string? data, int maxLength = 4096)
    {
        if (data is null || data.Length == 0 || data.Length > maxLength) return false;
        int i = 0;
        bool first = true;
        while (true)
        {
            SkipSeparators(data, ref i, comma: false);
            if (i == data.Length) return !first;
            char command = data[i++];
            int arity = char.ToUpperInvariant(command) switch
            {
                'M' or 'L' or 'T' => 2,
                'H' or 'V' => 1,
                'C' => 6,
                'S' or 'Q' => 4,
                'A' => 7,
                'Z' => 0,
                _ => -1,
            };
            if (arity < 0 || first && char.ToUpperInvariant(command) != 'M') return false;
            first = false;
            if (arity == 0) continue;
            int groups = 0;
            while (true)
            {
                int start = i;
                for (int n = 0; n < arity; n++)
                {
                    SkipSeparators(data, ref i, comma: n > 0 || groups > 0);
                    bool flag = char.ToUpperInvariant(command) == 'A' && n is 3 or 4;
                    if (!(flag ? Flag(data, ref i) : Number(data, ref i)))
                    {
                        if (n == 0 && groups > 0) { i = start; goto next; }
                        return false;
                    }
                }
                groups++;
            }
        next:;
        }
    }

    private static void SkipSeparators(string s, ref int i, bool comma)
    {
        bool seenComma = false;
        while (i < s.Length && (s[i] is ' ' or '\t' or '\n' or '\r' || comma && !seenComma && s[i] == ','))
        {
            if (s[i] == ',') seenComma = true;
            i++;
        }
    }

    private static bool Flag(string s, ref int i)
    {
        if (i < s.Length && s[i] is '0' or '1') { i++; return true; }
        return false;
    }

    private static bool Number(string s, ref int i)
    {
        int j = i;
        if (j < s.Length && s[j] is '+' or '-') j++;
        int digits = 0;
        while (j < s.Length && char.IsAsciiDigit(s[j])) { j++; digits++; }
        if (j < s.Length && s[j] == '.')
        {
            j++;
            while (j < s.Length && char.IsAsciiDigit(s[j])) { j++; digits++; }
        }
        if (digits == 0) return false;
        if (j < s.Length && s[j] is 'e' or 'E')
        {
            int k = j + 1;
            if (k < s.Length && s[k] is '+' or '-') k++;
            int exponent = 0;
            while (k < s.Length && char.IsAsciiDigit(s[k]) && exponent < 3) { k++; exponent++; }
            if (exponent == 0 || k < s.Length && char.IsAsciiDigit(s[k])) return false;
            j = k;
        }
        i = j;
        return true;
    }
}
