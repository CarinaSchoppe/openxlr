using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public sealed class PluginPresetViewModel(InsertViewModel target) : ViewModelBase
{
    internal InsertViewModel Target => target;
    public string Label => target.Label;
    public string Name { get; set; } = "";
    public EffectChainPreset? Selected { get; set; }
    public IReadOnlyList<EffectChainPreset> Presets { get; private set; } = [];
    public string? Error { get; private set; }
    internal bool Fits(EffectChainPreset preset) => preset.Chain.Inserts.Count == 1
        && preset.Chain.Inserts[0]?["plugin"]?.GetValue<string>() == target.Plugin
        && preset.Chain.Inserts[0]?["kind"]?.GetValue<string>() == target.Kind;
    internal void Refresh()
    {
        try { Presets = EffectChainPresets.Read().Where(Fits).ToArray(); Selected = null; Error = null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Error = ex.Message; }
        Raise(null);
    }
    internal void Save()
    {
        if (!target.Owner.Items.Contains(target)) { Fail("The effect is no longer active."); return; }
        try { EffectChainPresets.Save(Name, target.Owner.CaptureChain(target)); Refresh(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Fail(ex.Message); }
    }
    internal async Task LoadAsync()
    {
        if (Selected is not { } preset) return;
        try { await target.Owner.ApplySinglePresetAsync(target, preset.Chain); Fail(target.Owner.WorkflowError); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Fail(ex.Message); }
    }
    internal EffectChainPreset Export() => Selected ?? new(string.IsNullOrWhiteSpace(Name) ? "Current effect" : Name.Trim(), target.Owner.CaptureChain(target));
    internal void Import(EffectChainPreset preset)
    {
        if (!Fits(preset)) throw new InvalidDataException("This preset belongs to a different effect.");
        EffectChainPresets.Save(preset.Name, preset.Chain); Refresh();
    }
    internal void Delete(EffectChainPreset preset)
    {
        try { EffectChainPresets.Delete(preset.Name); Refresh(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Fail(ex.Message); }
    }
    internal void Fail(string? error) { Error = error; Raise(nameof(Error)); }
}

public partial class PluginPresetWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed, _busy;
    private InsertViewModel? _subscribed;
    private PluginPresetViewModel? Model => DataContext as PluginPresetViewModel;
    public PluginPresetWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (Model is not { } model) return;
            model.Refresh(); _subscribed = model.Target;
            _subscribed.Detached += OnTargetRemoved;
        };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel();
            if (_subscribed is { } target) target.Detached -= OnTargetRemoved;
        };
    }
    public PluginPresetWindow(InsertViewModel target) : this() => DataContext = new PluginPresetViewModel(target);
    private void OnTargetRemoved() => Close();
    private void OnSave(object? sender, RoutedEventArgs e) => Model?.Save();
    private async void OnLoad(object? sender, RoutedEventArgs e) { if (!_busy && Model is { } model) await model.LoadAsync(); }
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (Model is { Selected: { } preset } model && await Dialogs.ConfirmAsync(this, "Delete preset", $"Delete '{preset.Name}'?", "Delete") && !_closed)
            model.Delete(preset);
    }
    private async void OnImport(object? sender, RoutedEventArgs e) => await RunFileAsync(async () =>
    {
        if (await EffectPresetFiles.ImportAsync(this, _lifetime.Token) is { } preset && !_closed) Model?.Import(preset);
    });
    private async void OnExport(object? sender, RoutedEventArgs e) => await RunFileAsync(async () =>
    {
        if (Model is not { } model) return;
        await EffectPresetFiles.ExportAsync(this, model.Export(), _lifetime.Token);
        if (!_closed) Model?.Fail(null);
    });
    private async Task RunFileAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true; this.FindControl<StackPanel>("PresetControls")!.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) Model?.Fail("The preset operation timed out or was cancelled."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { if (!_closed) Model?.Fail(ex.Message); }
        finally { _busy = false; if (!_closed) this.FindControl<StackPanel>("PresetControls")!.IsEnabled = true; }
    }
}
