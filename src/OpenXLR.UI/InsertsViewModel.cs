using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>A plugin the picker offers (mono in / mono out only for the mic path).</summary>
public sealed record PluginChoice(string Uri, string Name, string Category, JsonNode Params)
{
    public override string ToString() => Category.Length > 0 ? $"{Name}  ({Category})" : Name;
}

/// <summary>
/// The insert chain of one channel: what the daemon reports, plus the
/// picker to add more. Edits go to the daemon as a whole new chain (order
/// matters); parameter moves go live one control at a time.
/// </summary>
public sealed class InsertsViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly string _channel;
    private bool _applying;
    private bool _pluginsRequested;

    public InsertsViewModel(DaemonClient client, string channel)
    {
        _client = client;
        _channel = channel;
    }

    public ObservableCollection<InsertViewModel> Items { get; } = [];
    public ObservableCollection<PluginChoice> PluginChoices { get; } = [];

    public bool HasItems => Items.Count > 0;

    private PluginChoice? _selectedPlugin;
    public PluginChoice? SelectedPlugin
    {
        get => _selectedPlugin;
        set { if (Set(ref _selectedPlugin, value)) Raise(nameof(CanAdd)); }
    }

    public bool CanAdd => _selectedPlugin is not null;

    private string? _note;
    /// <summary>Picker status: scanning, count, or why nothing is offered.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    /// <summary>Fetch the catalog once per connection (lilv's scan can take a moment).</summary>
    public async void EnsurePluginsLoaded()
    {
        if (_pluginsRequested) return;
        _pluginsRequested = true;
        Note = "Scanning LV2 plugins…";
        JsonNode? plugins = await _client.RequestPluginsAsync(TimeSpan.FromSeconds(20));
        Dispatcher.UIThread.Post(() =>
        {
            PluginChoices.Clear();
            if (plugins is not JsonArray arr) { Note = "Plugin list unavailable"; _pluginsRequested = false; return; }
            foreach (JsonNode? p in arr)
            {
                if (p is null) continue;
                // The mic path is mono: only mono in / mono out plugins fit.
                if ((p["audioIns"]?.GetValue<int>() ?? 0) != 1 || (p["audioOuts"]?.GetValue<int>() ?? 0) != 1) continue;
                PluginChoices.Add(new PluginChoice(
                    p["plugin"]!.GetValue<string>(),
                    p["name"]?.GetValue<string>() ?? p["plugin"]!.GetValue<string>(),
                    p["category"]?.GetValue<string>() ?? "",
                    p["params"] ?? new JsonArray()));
            }
            Note = PluginChoices.Count == 0
                ? "No mono LV2 plugins found (install e.g. lsp-plugins-lv2 or x42-plugins)"
                : $"{PluginChoices.Count} mono LV2 plugins available";
        });
    }

    public void ResetForNewConnection() => _pluginsRequested = false;

    /// <summary>Apply the daemon's view of this channel's chain.</summary>
    public void Apply(JsonNode? chain)
    {
        _applying = true;
        try
        {
            var incoming = chain as JsonArray ?? [];
            // Rebuild in place, keeping view models whose id survives so
            // expanded panels and slider state do not flicker.
            var byId = Items.ToDictionary(i => i.Id);
            var next = new List<InsertViewModel>();
            foreach (JsonNode? entry in incoming)
            {
                JsonNode? ins = entry?["insert"];
                if (ins is null) continue;
                string id = ins["id"]!.GetValue<string>();
                if (!byId.TryGetValue(id, out InsertViewModel? vm))
                    vm = new InsertViewModel(this, id, ins["plugin"]!.GetValue<string>(), ins["label"]?.GetValue<string>() ?? id);
                vm.ApplyFromDaemon(ins, entry?["error"]?.GetValue<string>());
                next.Add(vm);
            }
            if (!next.SequenceEqual(Items))
            {
                Items.Clear();
                foreach (InsertViewModel vm in next) Items.Add(vm);
                Raise(nameof(HasItems));
            }
        }
        finally { _applying = false; }
    }

    // --- edits, all expressed as a new whole chain ---

    public void Add()
    {
        if (_selectedPlugin is null) return;
        var chain = Snapshot();
        chain.Add(new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["kind"] = "lv2",
            ["plugin"] = _selectedPlugin.Uri,
            ["label"] = _selectedPlugin.Name,
            ["bypass"] = false,
            ["params"] = new Dictionary<string, double>(),
        });
        _ = _client.SetInsertsAsync(_channel, chain);
    }

    public void Remove(InsertViewModel item)
        => _ = _client.SetInsertsAsync(_channel, Snapshot(skip: item.Id));

    public void Move(InsertViewModel item, int delta)
    {
        int i = Items.IndexOf(item);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        var order = Items.ToList();
        (order[i], order[j]) = (order[j], order[i]);
        _ = _client.SetInsertsAsync(_channel, Snapshot(order));
    }

    internal void SendBypass(InsertViewModel item, bool bypass)
    {
        if (!_applying) _ = _client.SetInsertBypassAsync(_channel, item.Id, bypass);
    }

    internal void SendParam(InsertViewModel item, string symbol, double value)
    {
        if (_applying) return;
        string key = $"ins:{item.Id}:{symbol}";
        SliderSync.Touch(key);
        SliderSync.Send(key, () => _ = _client.SetInsertParamAsync(_channel, item.Id, symbol, value));
    }

    internal bool Applying => _applying;

    /// <summary>The current chain as the daemon wants it, minus an optional id.</summary>
    private List<object> Snapshot(IEnumerable<InsertViewModel>? order = null, string? skip = null)
        => [.. (order ?? Items).Where(i => i.Id != skip).Select(i => (object)i.ToPayload())];

    /// <summary>Parameter metadata for a plugin uri, from the catalog.</summary>
    internal JsonNode? ParamsFor(string uri) => PluginChoices.FirstOrDefault(p => p.Uri == uri)?.Params;
}

public sealed class InsertViewModel : ViewModelBase
{
    private readonly InsertsViewModel _owner;

    public InsertViewModel(InsertsViewModel owner, string id, string plugin, string label)
    {
        _owner = owner;
        Id = id;
        Plugin = plugin;
        Label = label;
    }

    public string Id { get; }
    public string Plugin { get; }
    public string Label { get; }

    private readonly Dictionary<string, double> _params = [];

    private bool _bypass;
    public bool Bypass
    {
        get => _bypass;
        set { if (Set(ref _bypass, value)) { Raise(nameof(StateText)); _owner.SendBypass(this, value); } }
    }

    private string? _error;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(StateText)); } } }
    public bool HasError => _error is not null;

    public string StateText => HasError ? "problem" : Bypass ? "bypassed" : "active";

    private bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value) && value && Params.Count == 0) BuildParams(); }
    }

    public ObservableCollection<InsertParamViewModel> Params { get; } = [];

    public void ApplyFromDaemon(JsonNode ins, string? error)
    {
        _bypass = ins["bypass"]?.GetValue<bool>() ?? false;
        Raise(nameof(Bypass));
        Error = error;
        _params.Clear();
        if (ins["params"] is JsonObject po)
            foreach ((string k, JsonNode? v) in po)
                if (v is not null) _params[k] = v.GetValue<double>();
        foreach (InsertParamViewModel p in Params)
            if (_params.TryGetValue(p.Symbol, out double v)) p.ApplyFromDaemon(v);
    }

    private void BuildParams()
    {
        if (_owner.ParamsFor(Plugin) is not JsonArray arr) return;
        foreach (JsonNode? p in arr)
        {
            if (p is null) continue;
            string sym = p["symbol"]!.GetValue<string>();
            var vm = new InsertParamViewModel(this, sym, p["name"]?.GetValue<string>() ?? sym,
                p["min"]?.GetValue<double>() ?? 0, p["max"]?.GetValue<double>() ?? 1,
                p["default"]?.GetValue<double>() ?? 0,
                p["toggled"]?.GetValue<bool>() ?? false, p["integer"]?.GetValue<bool>() ?? false,
                p["logarithmic"]?.GetValue<bool>() ?? false);
            if (_params.TryGetValue(sym, out double cur)) vm.ApplyFromDaemon(cur);
            Params.Add(vm);
        }
    }

    internal void SendParam(string symbol, double value)
    {
        _params[symbol] = value;
        _owner.SendParam(this, symbol, value);
    }

    internal object ToPayload() => new Dictionary<string, object?>
    {
        ["id"] = Id,
        ["kind"] = "lv2",
        ["plugin"] = Plugin,
        ["label"] = Label,
        ["bypass"] = _bypass,
        ["params"] = new Dictionary<string, double>(_params),
    };
}

/// <summary>One control port as a slider or switch.</summary>
public sealed class InsertParamViewModel : ViewModelBase
{
    private readonly InsertViewModel _owner;
    private bool _applying;

    public InsertParamViewModel(InsertViewModel owner, string symbol, string name,
        double min, double max, double def, bool toggled, bool integer, bool logarithmic)
    {
        _owner = owner;
        Symbol = symbol;
        Name = name;
        Min = min;
        Max = max;
        Toggled = toggled;
        Integer = integer;
        Logarithmic = logarithmic;
        _value = def;
    }

    public string Symbol { get; }
    public string Name { get; }
    public double Min { get; }
    public double Max { get; }
    public bool Toggled { get; }
    public bool Integer { get; }
    public bool Logarithmic { get; }
    public bool IsSlider => !Toggled;
    public double Step => Integer ? 1 : 0;

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            Raise(nameof(ValueText));
            Raise(nameof(On));
            if (!_applying) _owner.SendParam(Symbol, value);
        }
    }

    /// <summary>Toggle view of the value for switch ports.</summary>
    public bool On
    {
        get => _value >= 0.5;
        set => Value = value ? 1 : 0;
    }

    public string ValueText => Toggled ? (On ? "on" : "off")
        : Integer ? ((int)Math.Round(_value)).ToString()
        : Math.Abs(_value) >= 100 ? _value.ToString("0")
        : Math.Abs(_value) >= 10 ? _value.ToString("0.0")
        : _value.ToString("0.000");

    public void ApplyFromDaemon(double v)
    {
        _applying = true;
        try { Value = v; }
        finally { _applying = false; }
    }
}
