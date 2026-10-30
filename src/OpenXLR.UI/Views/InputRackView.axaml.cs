using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Layout;

namespace OpenXLR.UI.Views;

/// <summary>INPUTS as a rack of input strips. Its data context is an <see cref="InputRackViewModel"/>.</summary>
public partial class InputRackView : UserControl
{
    public InputRackView()
    {
        InitializeComponent();
    }

    /// <summary>The list the strips are drawn in, for the tests.</summary>
    internal ItemsControl Strips => RackStrips;

    private void OnXlrInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InputStripViewModel { Inserts: { } inserts, InsertsKey: { } key }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, inserts, key);
    }

    private void OnEq(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InputStripViewModel { Channel.Eq: { } eq }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            EqWindows.Show(window, eq);
    }

    private void OnCycleSoftLowCut(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InputStripViewModel strip) strip.Main.CycleSoftLowCut();
    }

    private async void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        // As in the INPUTS panel: a plugin running in its own process opens its own editor.
        if (insert.NativeEditorAvailable) await insert.Owner.ShowNativeEditorAsync(insert);
        else if (TopLevel.GetTopLevel((Control)sender!) is Window window) InsertWindows.OpenControls(window, insert);
    }
}
