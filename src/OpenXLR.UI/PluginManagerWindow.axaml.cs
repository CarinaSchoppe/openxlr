using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>Catalogue health, explicit rescans, and recovery for isolated plugins.</summary>
public partial class PluginManagerWindow : Window
{
    private readonly DaemonClient? _client;
    private readonly MainViewModel? _main;
    private IReadOnlyList<PluginCatalogItem> _items = [];

    public PluginManagerWindow() => InitializeComponent();

    internal PluginManagerWindow(DaemonClient client, MainViewModel main) : this()
    {
        _client = client;
        _main = main;
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        Opened += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_client is null) return;
        SetBusy(true, "Loading isolated catalogue…");
        JsonNode? result = await _client.RequestPluginsAsync(TimeSpan.FromSeconds(20));
        _items = result is JsonArray array
            ? [.. array.Where(node => node is not null).Select(node => PluginCatalogItem.FromJson(node!))]
            : [];
        ApplyFilter();
        int ready = _items.Count(item => item.Status == "ready");
        int unavailable = _items.Count - ready;
        SetBusy(false, result is null ? "Catalogue unavailable; check daemon diagnostics."
            : $"{ready} ready · {unavailable} unavailable");
    }

    private void ApplyFilter()
    {
        string query = FilterBox.Text?.Trim() ?? "";
        PluginList.ItemsSource = query.Length == 0 ? _items : _items.Where(item =>
            item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        SetBusy(true, "Scanning in isolated helper processes…");
        string? error = await _client.RescanPluginsAsync();
        if (error is not null) { SetBusy(false, error); return; }
        _main?.RefreshPluginCatalog();
        await RefreshAsync();
    }

    private async void OnRetry(object? sender, RoutedEventArgs e)
        => await RetryAsync((sender as Button)?.Tag as PluginCatalogItem, false);

    private async void OnUnquarantine(object? sender, RoutedEventArgs e)
        => await RetryAsync((sender as Button)?.Tag as PluginCatalogItem, true);

    private async Task RetryAsync(PluginCatalogItem? item, bool clearQuarantine)
    {
        if (item is null) return;
        if (_client is null) return;
        SetBusy(true, $"Retrying {item.Name}…");
        string? error = await _client.RetryPluginAsync(item.Id, clearQuarantine);
        if (error is not null) { SetBusy(false, error); return; }
        _main?.RefreshPluginCatalog();
        await RefreshAsync();
    }

    private void SetBusy(bool busy, string status)
    {
        RescanButton.IsEnabled = !busy;
        PluginList.IsEnabled = !busy;
        StatusText.Text = status;
    }
}

internal sealed record PluginCatalogItem(
    string Id, string Name, string Kind, string Category, string Status,
    bool HasNativeUi, bool SupportsState, int LatencySamples, string? Error)
{
    internal static PluginCatalogItem FromJson(JsonNode node)
        => new(node["plugin"]?.GetValue<string>() ?? "",
            node["name"]?.GetValue<string>() ?? "Unknown plugin",
            node["kind"]?.GetValue<string>() ?? "unknown",
            node["category"]?.GetValue<string>() ?? "",
            node["scanStatus"]?.GetValue<string>() ?? "ready",
            node["hasNativeUi"]?.GetValue<bool>() ?? false,
            node["supportsState"]?.GetValue<bool>() ?? false,
            node["latencySamples"]?.GetValue<int>() ?? 0,
            node["scanError"]?.GetValue<string>());

    public string KindLabel => Kind.ToUpperInvariant();
    public string StatusLabel => Status == "ready" ? "READY" : Status.ToUpperInvariant();
    public string CapabilityText => string.Join(" · ", new[]
    {
        Category.Length == 0 ? null : Category,
        HasNativeUi ? "native editor" : "generated controls",
        SupportsState ? "complete state" : "parameters only",
        LatencySamples > 0 ? $"{LatencySamples} samples latency" : "zero reported latency",
    }.Where(value => value is not null));
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool CanRetry => Status == "failed";
    public bool CanUnquarantine => Status == "quarantined";
    public string SearchText => $"{Name} {Kind} {Category} {Status} {Error}";
}
