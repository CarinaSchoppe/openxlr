using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// One insert's visual, parameter-aware controls in its own window (non-modal, one per
/// insert; the main window keeps them and closes them when the insert goes).
/// </summary>
public partial class InsertControlsWindow : Window
{
    public InsertControlsWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is not InsertViewModel insert) return;
            insert.EnsureParams();
            await insert.Owner.RefreshPresetsAsync();
        };
    }

    private void OnDefaults(object? sender, RoutedEventArgs e) => (DataContext as InsertViewModel)?.ResetToDefaults();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnNativeUi(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InsertViewModel insert) await insert.OpenNativeUiAsync();
    }

    private async void OnSavePreset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InsertViewModel insert) await insert.SavePluginPresetAsync();
    }

    private async void OnLoadPreset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InsertViewModel insert) await insert.LoadPluginPresetAsync();
    }
}
