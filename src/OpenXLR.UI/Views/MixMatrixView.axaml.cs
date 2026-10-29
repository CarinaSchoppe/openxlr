using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Layout;

namespace OpenXLR.UI.Views;

/// <summary>The submixer as a mix matrix. Its data context is a <see cref="MixMatrixViewModel"/>.</summary>
public partial class MixMatrixView : UserControl
{
    public MixMatrixView()
    {
        InitializeComponent();
    }

    /// <summary>The scroller the matrix moves sideways in when it is wider than its card, for the tests.</summary>
    internal ScrollViewer Scroller => MatrixScroll;

    private void OnXlrInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel { Inserts: { } inserts, InsertsKey: { } key }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, inserts, key);
    }

    private void OnEq(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel { Channel.Eq: { } eq }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            EqWindows.Show(window, eq);
    }

    /// <summary>The SFX row's Sounds button: the Soundboard window, under the window the matrix is in.</summary>
    private void OnSounds(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel row && TopLevel.GetTopLevel((Control)sender!) is Window window)
            SoundboardWindow.Open(window, row.Main.Soundboard);
    }

    /// <summary>XLR 1's software low cut in the processing flyout: off, 80 Hz, 120 Hz, as in the INPUTS panel.</summary>
    private void OnCycleSoftLowCut(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel row) row.Main.CycleSoftLowCut();
    }

    /// <summary>An input row's name and binding line: open its binding dialog.</summary>
    private void OnBinding(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel row
            && (row.Channel.IsBindable || row.Channel.CaptureSource is not null))
            _ = InputBindingDialog.OpenFrom((Control)sender!, row.Channel.Id);
    }

    private void OnMixInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixColumnViewModel column && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, column.Mix.Inserts, $"mix:{column.Mix.Id}");
    }
}
