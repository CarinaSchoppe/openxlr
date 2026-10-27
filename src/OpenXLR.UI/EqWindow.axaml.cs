using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// One microphone's EQ editor. Closing it ends a comparison, so the EQ is
/// never left bypassed by a window that is gone.
/// </summary>
public partial class EqWindow : Window
{
    public EqWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MicEqViewModel)?.EndComparison();
        SizeChanged += (_, e) => Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
    }

    /// <summary>Below this width the number fields drop their spin buttons, so three of them keep their values whole.</summary>
    public const double NarrowWidth = 760;

    /// <summary>The response graph.</summary>
    public EqGraph Graph => this.FindControl<EqGraph>("EqGraph")!;

    private void OnProcessOnUnit(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.ChooseProcessing("device");

    private void OnProcessInSoftware(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.ChooseProcessing("software");

    private void OnAddBand(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.AddBand();

    private void OnRemoveBand(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EqBandViewModel band }) band.Owner.RemoveBand(band);
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.Copy();

    private void OnPaste(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.Paste();

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MicEqViewModel eq) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("EqReset"), Localizer.Format("EqResetConfirm", eq.ChannelName), Localizer.Text("EqReset")))
            eq.Reset();
    }
}
