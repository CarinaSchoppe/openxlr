using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class EffectWorkflowWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _filesBusy, _closed;
    public EffectWorkflowWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Chain?.ReadPresets();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
    }
    private InsertsViewModel? Chain => DataContext as InsertsViewModel;
    private void OnCopy(object? sender, RoutedEventArgs e) => Chain?.CopyEffects();
    private async void OnPaste(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.PasteEffectsAsync(false); }
    private async void OnReplace(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.PasteEffectsAsync(true); }
    private void OnStoreA(object? sender, RoutedEventArgs e) => Chain?.StoreComparison(false);
    private void OnStoreB(object? sender, RoutedEventArgs e) => Chain?.StoreComparison(true);
    private async void OnHearA(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.HearComparisonAsync(false); }
    private async void OnHearB(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.HearComparisonAsync(true); }
    private void OnSave(object? sender, RoutedEventArgs e) => Chain?.SavePreset();
    private async void OnLoad(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.LoadPresetAsync(); }
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Chain is { SelectedPreset: { } preset } chain
            && await Dialogs.ConfirmAsync(this, "Delete preset", $"Delete the saved chain '{preset.Name}'? The live chain is kept.", "Delete"))
            if (!_closed && ReferenceEquals(chain.SelectedPreset, preset)) chain.DeletePreset();
    }
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_closed || _filesBusy || Chain is not { } chain || !chain.CanEditEffects) return;
        _filesBusy = true;
        try
        {
            var preset = await EffectPresetFiles.ImportAsync(this, _lifetime.Token);
            if (preset is null || _closed) return;
            EffectChainPresets.Save(preset.Name, preset.Chain); chain.ReadPresets();
        }
        catch (OperationCanceledException) { if (!_closed) chain.ReportWorkflowError("The preset operation timed out or was cancelled."); }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.UnauthorizedAccessException or System.Text.Json.JsonException or System.NotSupportedException)
        { if (!_closed) chain.ReportWorkflowError(ex.Message); }
        finally { _filesBusy = false; }
    }
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_closed || _filesBusy || Chain is not { } chain || !chain.CanEditEffects) return;
        _filesBusy = true;
        try
        {
            var preset = chain.SelectedPreset ?? new EffectChainPreset(string.IsNullOrWhiteSpace(chain.PresetName) ? "Current chain" : chain.PresetName.Trim(), chain.CaptureChain());
            await EffectPresetFiles.ExportAsync(this, preset, _lifetime.Token);
            chain.ReportWorkflowError(null);
        }
        catch (OperationCanceledException) { if (!_closed) chain.ReportWorkflowError("The preset operation timed out or was cancelled."); }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.UnauthorizedAccessException or System.Text.Json.JsonException or System.NotSupportedException)
        { if (!_closed) chain.ReportWorkflowError(ex.Message); }
        finally { _filesBusy = false; }
    }
    private void OnRefresh(object? sender, RoutedEventArgs e) => Chain?.ReadPresets();
}
