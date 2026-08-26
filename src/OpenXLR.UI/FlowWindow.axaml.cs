using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>
/// A Wave Link style flow graph of the live routing, rebuilt from the view
/// model on every state push: sources (apps and hardware inputs) into
/// channels, channels into mixes, mixes to physical and virtual outputs.
/// Read-only by design; the mixer cards are where things are changed.
/// </summary>
public partial class FlowWindow : Window
{
    private const double NodeW = 190, NodeH = 40, ColGap = 130, RowGap = 10, Pad = 8;

    private static readonly IBrush NodeBg = new SolidColorBrush(Color.Parse("#23262f"));
    private static readonly IBrush NodeBgDim = new SolidColorBrush(Color.Parse("#1c1f26"));
    private static readonly IBrush TextFg = new SolidColorBrush(Color.Parse("#e6e9f0"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#7d8496"));
    private static readonly IBrush EdgeMuted = new SolidColorBrush(Color.Parse("#555b68"));

    // One stable hue per channel: every edge touching a channel wears its
    // color, which is what keeps the crossing curves readable.
    private static readonly IBrush[] Palette =
        new[] { "#4fb3d9", "#e0a84a", "#b06ab3", "#3ecf7a", "#e06767",
                "#6a8de0", "#d9cf4f", "#e08bc7", "#7fd9c6", "#c78b5a" }
        .Select(IBrush (h) => new SolidColorBrush(Color.Parse(h))).ToArray();

    private readonly MainViewModel? _vm;

    public FlowWindow() => InitializeComponent();

    public FlowWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        vm.StateApplied += Rebuild;
        Opened += (_, _) => Rebuild();
        Closed += (_, _) => vm.StateApplied -= Rebuild;
    }

    private void Rebuild()
    {
        if (_vm is null) return;
        Canvas c = GraphCanvas;
        c.Children.Clear();

        // ---- collect the nodes per column ----
        var sources = new List<(string Key, string Label, string Channel, bool Playing)>();
        foreach (string chId in new[] { "xlr1", "xlr2", "aux" })
            if (_vm.Channels.Any(x => x.Id == chId))
                sources.Add(($"hw:{chId}",
                    chId switch { "xlr1" => "XLR 1 jack", "xlr2" => "XLR 2 jack", _ => "Line In / USB Aux" },
                    chId, true));
        foreach (AppStreamViewModel a in _vm.ActiveApps)
            sources.Add(($"app:{a.Identity}", a.Label, a.ChannelId, a.Active));

        var channels = _vm.Channels.ToList();
        var mixes = _vm.Mixes.ToList();

        var outputs = new List<(string Key, string Label, string MixId, bool Active)>();
        foreach (MonitorOutputItem o in _vm.MonitorOutputs.Where(o => o.IsSelected))
            outputs.Add(($"out:{o.Name}", o.Label, "monitor", true));
        outputs.Add(("vm:stream", "OpenXLR Stream (virtual mic)", "stream", true));
        outputs.Add(("vm:chat", "OpenXLR Chat (virtual mic)", "chat", true));
        MixViewModel? auxMix = mixes.FirstOrDefault(m => m.IsAuxPort);
        if (auxMix is not null)
            outputs.Add(("aux:port", "USB Aux port (second PC)", auxMix.Id, auxMix.AuxPortEnabled));

        // ---- layout ----
        double colX(int col) => Pad + col * (NodeW + ColGap);
        var pos = new Dictionary<string, Rect>();
        void Place(int col, int row, string key)
            => pos[key] = new Rect(colX(col), Pad + row * (NodeH + RowGap), NodeW, NodeH);

        for (int i = 0; i < sources.Count; i++) Place(0, i, sources[i].Key);
        for (int i = 0; i < channels.Count; i++) Place(1, i, $"ch:{channels[i].Id}");
        for (int i = 0; i < mixes.Count; i++) Place(2, i * 2 + 1, $"mix:{mixes[i].Id}");
        for (int i = 0; i < outputs.Count; i++) Place(3, i * 2 + 1, outputs[i].Key);

        c.Width = colX(3) + NodeW + Pad;
        c.Height = pos.Values.Max(r => r.Bottom) + Pad;

        // ---- edges first (under the nodes) ----
        IBrush ChannelBrush(string chId)
        {
            int idx = channels.FindIndex(x => x.Id == chId);
            return idx < 0 ? EdgeMuted : Palette[idx % Palette.Length];
        }
        IBrush MixBrush(string mixId)
        {
            int idx = mixes.FindIndex(x => x.Id == mixId);
            return idx < 0 ? EdgeMuted : Palette[idx % Palette.Length];
        }

        foreach (var s in sources)
            if (pos.ContainsKey($"ch:{s.Channel}"))
                Edge(c, pos[s.Key], pos[$"ch:{s.Channel}"],
                    s.Playing ? ChannelBrush(s.Channel) : EdgeMuted, s.Playing ? 2 : 1.2, dashed: !s.Playing);

        foreach (ChannelViewModel ch in channels)
            foreach (SendViewModel send in ch.Sends)
            {
                if (!pos.ContainsKey($"mix:{send.MixId}")) continue;
                bool flows = send.Level > 0.001 && !send.Muted;
                if (!flows && send.Level <= 0.001) continue;      // no send at all: no line
                Edge(c, pos[$"ch:{ch.Id}"], pos[$"mix:{send.MixId}"],
                    flows ? ChannelBrush(ch.Id) : EdgeMuted,
                    flows ? Math.Max(1.2, send.Level * 3.0) : 1.2, dashed: !flows);
            }

        foreach (var o in outputs)
            if (pos.ContainsKey($"mix:{o.MixId}"))
                Edge(c, pos[$"mix:{o.MixId}"], pos[o.Key], o.Active ? MixBrush(o.MixId) : EdgeMuted,
                    o.Active ? 2 : 1.2, dashed: !o.Active);

        // ---- nodes ----
        foreach (var s in sources) Node(c, pos[s.Key], s.Label, s.Playing ? null : "silent", s.Playing);
        foreach (ChannelViewModel ch in channels)
            Node(c, pos[$"ch:{ch.Id}"], ch.Name, null, true, ChannelBrush(ch.Id));
        foreach (MixViewModel m in mixes)
            Node(c, pos[$"mix:{m.Id}"], m.Name, m.Muted ? "muted" : $"{m.Volume * 100:0}%", !m.Muted, MixBrush(m.Id));
        foreach (var o in outputs) Node(c, pos[o.Key], o.Label, o.Active ? null : "off", o.Active);

        // ---- column headers ----
        string[] headers = ["SOURCES", "CHANNELS", "MIXES", "OUTPUTS"];
        for (int i = 0; i < headers.Length; i++)
        {
            var t = new TextBlock
            {
                Text = headers[i], FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = TextDim,
            };
            Canvas.SetLeft(t, colX(i));
            Canvas.SetTop(t, c.Height);
            c.Children.Add(t);
        }
        c.Height += 22;
    }

    private static void Node(Canvas c, Rect r, string label, string? sub, bool lit, IBrush? accent = null)
    {
        var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, Foreground = lit ? TextFg : TextDim,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (sub is not null)
            text.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = TextDim });

        var node = new Border
        {
            Width = r.Width, Height = r.Height,
            Background = lit ? NodeBg : NodeBgDim,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = accent ?? Brushes.Transparent,
            Child = text,
        };
        Canvas.SetLeft(node, r.X);
        Canvas.SetTop(node, r.Y);
        c.Children.Add(node);
    }

    private static void Edge(Canvas c, Rect from, Rect to, IBrush stroke, double thickness, bool dashed)
    {
        var p0 = new Point(from.Right, from.Center.Y);
        var p3 = new Point(to.X, to.Center.Y);
        double bend = (p3.X - p0.X) * 0.5;
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(p0, isFilled: false);
            ctx.CubicBezierTo(new Point(p0.X + bend, p0.Y), new Point(p3.X - bend, p3.Y), p3);
        }
        c.Children.Add(new Path
        {
            Data = geo, Stroke = stroke, StrokeThickness = thickness,
            StrokeDashArray = dashed ? [3, 3] : null,
            Opacity = dashed ? 0.7 : 0.9,
        });
    }
}
