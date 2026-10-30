using System;
using System.Collections.Generic;
using System.Globalization;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// One colour of the built-in palette: a name clients turn into their own
/// words, and the #RRGGBB value a layout or a clip stores when it is picked.
/// </summary>
public sealed record LayoutColourDefinition(string Name, string Hex);

/// <summary>
/// The colours offered beside the colour field wherever a channel, a mix, a
/// headphone output or a sound clip takes one. A pick is stored as its hex
/// value, exactly as a typed one, so the palette is a convenience and not a
/// list a value has to come from. Shared as a linked source, like the icon
/// set, so every client offers the same colours.
/// </summary>
public static class LayoutColours
{
    /// <summary>
    /// The palette, in the order a picker offers it: saturated enough to
    /// fill a soundboard pad, and far enough apart to tell at a glance.
    /// </summary>
    public static IReadOnlyList<LayoutColourDefinition> Palette { get; } =
    [
        new("red", "#D32F2F"),
        new("orange", "#F57C00"),
        new("yellow", "#FBC02D"),
        new("lime", "#8BC34A"),
        new("green", "#2E7D32"),
        new("teal", "#00796B"),
        new("cyan", "#00ACC1"),
        new("blue", "#1976D2"),
        new("indigo", "#3F51B5"),
        new("purple", "#8E24AA"),
        new("pink", "#D81B60"),
        new("brown", "#795548"),
    ];

    /// <summary>
    /// The relative luminance, as WCAG defines it, above which dark lettering
    /// reads better than white on a colour: the point where the two contrast
    /// ratios meet against near-black lettering.
    /// </summary>
    public const double LightLuminance = 0.2;

    /// <summary>A <c>#</c> and six hexadecimal digits, the one form a stored colour takes.</summary>
    public static bool IsHex(string? text) =>
        text is { Length: 7 } && text[0] == '#' && text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    /// <summary>The palette entry with this value, in either case, or null for a colour of the user's own.</summary>
    public static LayoutColourDefinition? Find(string? hex)
    {
        foreach (LayoutColourDefinition colour in Palette)
            if (string.Equals(colour.Hex, hex, StringComparison.OrdinalIgnoreCase)) return colour;
        return null;
    }

    /// <summary>The relative luminance of a #RRGGBB colour, 0 for black to 1 for white; null for anything else.</summary>
    public static double? Luminance(string? hex)
    {
        if (!IsHex(hex)) return null;
        double Channel(int at)
        {
            double value = int.Parse(hex!.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    /// <summary>True when dark lettering reads better than light on this colour.</summary>
    public static bool IsLight(string? hex) => Luminance(hex) is double luminance && luminance > LightLuminance;
}
