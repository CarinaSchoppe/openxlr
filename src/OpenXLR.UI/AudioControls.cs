using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>A compact DAW-style rotary control with vertical drag and wheel input.</summary>
public sealed class ArcKnob : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ArcKnob, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ArcKnob, double>(nameof(Maximum), 1);
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ArcKnob, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsLogarithmicProperty =
        AvaloniaProperty.Register<ArcKnob, bool>(nameof(IsLogarithmic));
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<ArcKnob, double>(nameof(Step));

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, Snap(value)); }
    public bool IsLogarithmic { get => GetValue(IsLogarithmicProperty); set => SetValue(IsLogarithmicProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    private bool _dragging;
    private double _startY;
    private double _startNormal;

    static ArcKnob() => AffectsRender<ArcKnob>(MinimumProperty, MaximumProperty, ValueProperty, IsLogarithmicProperty);
    public ArcKnob() => Focusable = true;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _dragging = true;
        _startY = e.GetPosition(this).Y;
        _startNormal = Normalized(Value);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        double normal = Math.Clamp(_startNormal + (_startY - e.GetPosition(this).Y) / 140.0, 0, 1);
        SetCurrentValue(ValueProperty, Snap(FromNormalized(normal)));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetCurrentValue(ValueProperty, Snap(FromNormalized(Math.Clamp(Normalized(Value) + Math.Sign(e.Delta.Y) * 0.02, 0, 1))));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double? next = e.Key switch
        {
            Key.Home => Minimum,
            Key.End => Maximum,
            Key.Up or Key.Right => Step > 0 ? Value + Step : FromNormalized(Math.Clamp(Normalized(Value) + 0.01, 0, 1)),
            Key.Down or Key.Left => Step > 0 ? Value - Step : FromNormalized(Math.Clamp(Normalized(Value) - 0.01, 0, 1)),
            _ => null,
        };
        if (next is null) return;
        SetCurrentValue(ValueProperty, Snap(next.Value));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 8) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = size * 0.39;
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#20242c")),
            new Pen(new SolidColorBrush(Color.Parse("#414754")), 1), centre, radius, radius);
        DrawArc(context, centre, radius + 4, 0, 1, new Pen(new SolidColorBrush(Color.Parse("#343a46")), 4));
        DrawArc(context, centre, radius + 4, 0, Normalized(Value),
            new Pen(new SolidColorBrush(Color.Parse("#4fb3d9")), 4));

        double angle = (-225 + Normalized(Value) * 270) * Math.PI / 180;
        var tip = new Point(centre.X + Math.Cos(angle) * radius * 0.72,
                            centre.Y + Math.Sin(angle) * radius * 0.72);
        context.DrawLine(new Pen(Brushes.White, 2), centre, tip);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#e6e9f0")), null, centre, 2.3, 2.3);
    }

    private static void DrawArc(DrawingContext context, Point centre, double radius,
        double from, double to, Pen pen)
    {
        if (to <= from) return;
        var geometry = new StreamGeometry();
        using StreamGeometryContext path = geometry.Open();
        for (int i = 0; i <= 48; i++)
        {
            double t = from + (to - from) * i / 48.0;
            double angle = (-225 + t * 270) * Math.PI / 180;
            var point = new Point(centre.X + Math.Cos(angle) * radius, centre.Y + Math.Sin(angle) * radius);
            if (i == 0) path.BeginFigure(point, false); else path.LineTo(point);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private double Normalized(double value)
    {
        if (Maximum <= Minimum) return 0;
        if (IsLogarithmic && Maximum > 0)
        {
            double floor = Minimum > 0 ? Minimum : Math.Max(Maximum * 0.0001, 0.000001);
            if (value <= Minimum) return 0;
            double mapped = Math.Log(Math.Max(value, floor) / floor) / Math.Log(Maximum / floor);
            return Minimum > 0 ? Math.Clamp(mapped, 0, 1) : 0.04 + Math.Clamp(mapped, 0, 1) * 0.96;
        }
        return Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
    }

    private double FromNormalized(double normal)
    {
        if (IsLogarithmic && Maximum > 0)
        {
            double floor = Minimum > 0 ? Minimum : Math.Max(Maximum * 0.0001, 0.000001);
            if (Minimum <= 0 && normal <= 0.04) return Minimum;
            double mapped = Minimum > 0 ? normal : (normal - 0.04) / 0.96;
            return floor * Math.Pow(Maximum / floor, Math.Clamp(mapped, 0, 1));
        }
        return Minimum + (Maximum - Minimum) * normal;
    }

    private double Snap(double value)
    {
        value = Math.Clamp(value, Math.Min(Minimum, Maximum), Math.Max(Minimum, Maximum));
        return Step > 0 ? Math.Clamp(Minimum + Math.Round((value - Minimum) / Step) * Step, Minimum, Maximum) : value;
    }
}

/// <summary>
/// Parameter overview, not an FFT or measured response. EQ bars show configured
/// band gains; dynamics bars show actual threshold settings. PipeWire does not
/// expose LSP's native UI/analysis transport to this client.
/// </summary>
public sealed class PluginVisualizer : Control
{
    public static readonly StyledProperty<InsertViewModel?> InsertProperty =
        AvaloniaProperty.Register<PluginVisualizer, InsertViewModel?>(nameof(Insert));

    public InsertViewModel? Insert { get => GetValue(InsertProperty); set => SetValue(InsertProperty, value); }
    private InsertViewModel? _subscribed;

    static PluginVisualizer() => AffectsRender<PluginVisualizer>(InsertProperty);

    public PluginVisualizer()
    {
        DetachedFromVisualTree += (_, _) => Detach();
        AttachedToVisualTree += (_, _) => Attach(Insert);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != InsertProperty) return;
        Detach();
        Attach(change.NewValue as InsertViewModel);
    }

    private void Attach(InsertViewModel? insert)
    {
        if (insert is null || ReferenceEquals(_subscribed, insert)) return;
        _subscribed = insert;
        _subscribed.Params.CollectionChanged += ParamsChanged;
        foreach (InsertParamViewModel parameter in _subscribed.Params) parameter.PropertyChanged += ParameterChanged;
        InvalidateVisual();
    }

    private void ParamsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (InsertParamViewModel p in e.OldItems) p.PropertyChanged -= ParameterChanged;
        if (e.NewItems is not null)
            foreach (InsertParamViewModel p in e.NewItems) p.PropertyChanged += ParameterChanged;
        InvalidateVisual();
    }

    private void ParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InsertParamViewModel.Value) or nameof(InsertParamViewModel.On))
            InvalidateVisual();
    }

    private void Detach()
    {
        if (_subscribed is null) return;
        _subscribed.Params.CollectionChanged -= ParamsChanged;
        foreach (InsertParamViewModel parameter in _subscribed.Params) parameter.PropertyChanged -= ParameterChanged;
        _subscribed = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect area = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#111319")),
            new Pen(new SolidColorBrush(Color.Parse("#2d313c")), 1), area, 7, 7);
        if (Insert is null || Bounds.Width < 40 || Bounds.Height < 40) return;
        Rect plot = new Rect(22, 14, Math.Max(1, Bounds.Width - 38), Math.Max(1, Bounds.Height - 30));
        DrawGrid(context, plot);
        if (Insert.IsEqualizer) DrawEqualizer(context, plot, Insert.Params);
        else if (Insert.IsDynamics) DrawDynamics(context, plot, Insert.Params);
    }

    private static void DrawGrid(DrawingContext context, Rect plot)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#252a33")), 1);
        for (int i = 0; i <= 6; i++)
            context.DrawLine(pen, new Point(plot.X + plot.Width * i / 6, plot.Y),
                new Point(plot.X + plot.Width * i / 6, plot.Bottom));
        for (int i = 0; i <= 4; i++)
            context.DrawLine(pen, new Point(plot.X, plot.Y + plot.Height * i / 4),
                new Point(plot.Right, plot.Y + plot.Height * i / 4));
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#4a5261")), 1),
            new Point(plot.X, plot.Center.Y), new Point(plot.Right, plot.Center.Y));
    }

    private static void DrawEqualizer(DrawingContext context, Rect plot,
        IEnumerable<InsertParamViewModel> parameters)
    {
        List<InsertParamViewModel> all = parameters.ToList();
        var bands = new List<(double Frequency, double GainDb)>();
        foreach (InsertParamViewModel frequency in all.Where(p =>
                     p.Name.StartsWith("Frequency ", StringComparison.OrdinalIgnoreCase)))
        {
            string suffix = frequency.Name["Frequency ".Length..];
            InsertParamViewModel? gain = all.FirstOrDefault(p =>
                p.Name.Equals("Gain " + suffix, StringComparison.OrdinalIgnoreCase));
            if (gain is null) continue;
            InsertParamViewModel? type = all.FirstOrDefault(p => p.Name.Equals("Filter type " + suffix, StringComparison.OrdinalIgnoreCase));
            if (type?.Value == 0) continue; // LSP's disabled bands are not active EQ stages.
            double gainDb = gain.Decibels;
            bands.Add((Math.Clamp(frequency.Value, 20, 20000), Math.Clamp(gainDb, -24, 24)));
        }

        // LSP's graphic equalizer exposes fixed bands named "Band gain 25",
        // "Band gain 1K", etc. Draw them as real bars and connect their tops.
        var fixedBands = all.Where(p => p.Name.StartsWith("Band gain ", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Frequency: ParseFrequency(p.Name["Band gain ".Length..]),
                          GainDb: p.Decibels))
            .Where(b => b.Frequency is >= 20 and <= 20000)
            .OrderBy(b => b.Frequency).ToList();
        if (fixedBands.Count > 0)
        {
            var bandGeometry = new StreamGeometry();
            using (StreamGeometryContext path = bandGeometry.Open())
            {
                for (int i = 0; i < fixedBands.Count; i++)
                {
                    (double frequency, double gainDb) = fixedBands[i];
                    double x = Math.Log(frequency / 20, 1000);
                    var point = new Point(plot.X + x * plot.Width,
                        plot.Center.Y - Math.Clamp(gainDb, -24, 24) / 48 * plot.Height);
                    if (i == 0) path.BeginFigure(point, false); else path.LineTo(point);
                    context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#294f60")), 3),
                        new Point(point.X, plot.Center.Y), point);
                    context.DrawEllipse(new SolidColorBrush(Color.Parse("#4fb3d9")),
                        new Pen(Brushes.White, 1), point, 3.5, 3.5);
                }
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#4fb3d9")), 2.5), bandGeometry);
            return;
        }

        // Do not invent a Gaussian frequency response: LSP can use shelves,
        // pass filters and several processing modes. Mark only known settings.
        foreach ((double frequency, double gain) in bands)
        {
            double x = Math.Log(frequency / 20, 1000);
            var point = new Point(plot.X + x * plot.Width,
                plot.Center.Y - Math.Clamp(gain, -24, 24) / 48 * plot.Height);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#4fb3d9")), 3),
                new Point(point.X, plot.Center.Y), point);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#4fb3d9")), new Pen(Brushes.White, 1), point, 4, 4);
        }

        static double ParseFrequency(string text)
        {
            text = text.Trim();
            double multiplier = text.EndsWith("K", StringComparison.OrdinalIgnoreCase) ? 1000 : 1;
            if (multiplier > 1) text = text[..^1];
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value * multiplier : 0;
        }
    }

    private static void DrawDynamics(DrawingContext context, Rect plot,
        IEnumerable<InsertParamViewModel> parameters)
    {
        // Thresholds have distinct meanings for gates, upward compressors and
        // limiters; a universal downward-compressor curve would be incorrect.
        var thresholds = parameters.Where(p => p.Name.Contains("threshold", StringComparison.OrdinalIgnoreCase)
            && p.Unit == "dB" && !p.Toggled && !p.Enumeration).Take(6).ToList();
        for (int i = 0; i < thresholds.Count; i++)
        {
            InsertParamViewModel parameter = thresholds[i];
            double y = plot.Y + (i + 0.5) * plot.Height / Math.Max(1, thresholds.Count);
            double x = plot.X + (Math.Clamp(parameter.Decibels, -60, 0) + 60) / 60 * plot.Width;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#e0a84a")), 5),
                new Point(plot.X, y), new Point(x, y));
            var label = new FormattedText($"{parameter.Name}: {parameter.ValueText}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Inter"), 11, Brushes.White);
            context.DrawText(label, new Point(plot.X, y - 19));
        }
    }
}
