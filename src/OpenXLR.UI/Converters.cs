using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpenXLR.UI;

public static class Converters
{
    /// <summary>
    /// Meter fill as a horizontal scale of a full-width bar, so the same
    /// template fills a narrow channel strip and a wide mix master alike.
    /// </summary>
    public static readonly IValueConverter MeterScale =
        new FuncValueConverter<double, ITransform>(v =>
            new ScaleTransform(Math.Clamp(v, 0, 1), 1.0));

    /// <summary>Connection dot: green when connected, grey when not.</summary>
    public static readonly IValueConverter BoolToBrush =
        new FuncValueConverter<bool, IBrush>(on =>
            new SolidColorBrush(on ? Color.Parse("#3ecf7a") : Color.Parse("#4a4f5c")));

    /// <summary>Insert LED: green while processing, red when bypassed or failed.</summary>
    public static readonly IValueConverter ActiveLed =
        new FuncValueConverter<bool, IBrush>(on =>
            new SolidColorBrush(on ? Color.Parse("#3ecf7a") : Color.Parse("#ff3c4e")));

    /// <summary>Tile-header chevron: up when the tile is expanded.</summary>
    public static readonly IValueConverter Chevron =
        new FuncValueConverter<bool, string>(expanded => expanded ? "⌃" : "⌄");
}
