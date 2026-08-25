using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpenXLR.UI;

public static class Converters
{
    /// <summary>Meter bar width in a 110 px track, from a 0..1 peak.</summary>
    public static readonly IValueConverter MeterWidth =
        new FuncValueConverter<double, double>(v => Math.Clamp(v, 0, 1) * 110);

    /// <summary>Connection dot: green when connected, grey when not.</summary>
    public static readonly IValueConverter BoolToBrush =
        new FuncValueConverter<bool, IBrush>(on =>
            new SolidColorBrush(on ? Color.Parse("#3ecf7a") : Color.Parse("#4a4f5c")));
}
