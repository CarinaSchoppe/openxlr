using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>A mix's insert chain in its own window: add, reorder, bypass, remove, open controls.</summary>
public partial class MixInsertsWindow : Window
{
    public MixInsertsWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (Chain is not null) await Chain.RefreshPresetsAsync();
        };
    }

    private InsertsViewModel? Chain => DataContext as InsertsViewModel;

    private async void OnAddInsert(object? sender, RoutedEventArgs e)
    {
        if (Chain is null) return;
        var picker = new PluginPickerWindow { DataContext = Chain };
        PluginChoice? choice = await picker.ShowDialog<PluginChoice?>(this);
        if (choice is not null) Chain.Add(choice);
    }

    private void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) InsertWindows.OpenControls(this, ins);
    }

    private void OnInsertUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, -1);
    }

    private void OnInsertDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, +1);
    }

    private void OnInsertRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Remove(ins);
    }

    private async void OnInsertRetry(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel insert)
            _ = await insert.RetryAsync(clearQuarantine: false);
    }

    private async void OnInsertUnquarantine(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel insert)
            _ = await insert.RetryAsync(clearQuarantine: true);
    }

    private async void OnSavePreset(object? sender, RoutedEventArgs e)
    {
        if (Chain is not null) await Chain.SaveChainPresetAsync();
    }

    private async void OnLoadPreset(object? sender, RoutedEventArgs e)
    {
        if (Chain is not null) await Chain.LoadChainPresetAsync();
    }

    private async void OnDeletePreset(object? sender, RoutedEventArgs e)
    {
        if (Chain is not null) await Chain.DeleteChainPresetAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
