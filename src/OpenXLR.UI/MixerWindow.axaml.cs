using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>The main window's mixer panel, moved here without creating another client or mixer.</summary>
public partial class MixerWindow : Window
{
    private Control? _panel;

    public MixerWindow()
    {
        InitializeComponent();
        UpdateControlSizing();
        SkinService.Changed += UpdateControlSizing;
        MixerScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty) UpdatePanelWidth();
        };
        Closed += (_, _) => SkinService.Changed -= UpdateControlSizing;
    }

    internal void Attach(Control panel, object? model, Window origin)
    {
        DataContext = model;
        _panel = panel;
        MixerScroll.Content = panel;
        UpdatePanelWidth();
        // Start on the originating screen, with enough room for its frame.
        // The window stays unowned, so hiding the main window leaves this one usable.
        if (origin.Screens.ScreenFromWindow(origin) is { } screen)
        {
            Size frame = origin.FrameSize ?? origin.ClientSize;
            var decoration = new Size(Math.Max(0, frame.Width - origin.ClientSize.Width),
                Math.Max(0, frame.Height - origin.ClientSize.Height));
            var size = FlowWindow.FitClientSize(new(Width, Height), screen.WorkingArea.Size, screen.Scaling, decoration);
            MinWidth = Math.Min(MinWidth, size.Width);
            MinHeight = Math.Min(MinHeight, size.Height);
            Width = size.Width;
            Height = size.Height;
            Position = screen.WorkingArea.Position;
        }
    }

    internal Control Detach()
    {
        var panel = (Control)MixerScroll.Content!;
        MixerScroll.Content = null;
        _panel = null;
        panel.ClearValue(WidthProperty);
        DataContext = null;
        return panel;
    }

    private void UpdateControlSizing()
    {
        Classes.Set("large-targets", SkinService.LargeTargets);
        UpdatePanelWidth();
    }

    private void UpdatePanelWidth()
    {
        if (_panel is null) return;
        // Keep wrapping at the viewport width. A skin with wider tiles can
        // still scroll sideways, instead of clipping the tile's controls.
        double tileWidth = Math.Max((double)this.FindResource("Ox.Mixer.MixWidth")!,
            (double)this.FindResource("Ox.Mixer.ChannelWidth")!);
        _panel.Width = Math.Max(MixerScroll.Viewport.Width, tileWidth + 8);
    }

    private void OnDockMixer(object? sender, RoutedEventArgs e) => Close();
}
