using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>An LV2 or VST3 plug-in the picker offers for this path width.</summary>
public sealed record PluginChoice(string Uri, string Name, string Category, JsonNode Params,
    bool HasNativeUi = false, string Kind = "lv2", JsonNode? AuxiliaryInputs = null)
{
    public override string ToString()
        => Category.Length > 0 ? $"{Name}  [{Kind.ToUpperInvariant()} · {Category}]"
            : $"{Name}  [{Kind.ToUpperInvariant()}]";
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
    private readonly int _channels;
    private bool _applying;
    private bool _pluginsRequested;
    private bool _catalogLoaded;
    public bool NativeUiSupported { get; set; }

    /// <param name="channel">Insert key: a channel ID or "mix:&lt;id&gt;".</param>
    /// <param name="channels">1 for a mono mic, 2 for an application, Aux In or output mix.</param>
    public InsertsViewModel(DaemonClient client, string channel, int channels = 1, string? title = null)
    {
        _client = client;
        _channel = channel;
        _channels = channels;
        _title = title ?? channel;
    }

    /// <summary>What the chain belongs to, for window titles ("XLR 1", "Stream mix").</summary>
    private string _title;
    public string Title { get => _title; private set => Set(ref _title, value); }

    public void SetTitle(string title) => Title = title;

    /// <summary>Picker header: which plugins fit this chain.</summary>
    public string PickerHint => _channels == 1
        ? "LV2 and VST3 plugins that fit the mono mic path (one input, one output)"
        : "LV2 and VST3 plugins that fit a stereo path (two inputs, two outputs)";

    public ObservableCollection<InsertViewModel> Items { get; } = [];
    public ObservableCollection<PluginChoice> PluginChoices { get; } = [];
    public ObservableCollection<string> ChainPresetNames { get; } = [];
    public ObservableCollection<string> PluginPresetNames { get; } = [];
    public ObservableCollection<SidechainSourceChoice> SidechainSources { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>One-line state for the strip: count, or a hint when empty.</summary>
    public string Summary => Items.Count switch
    {
        0 => "none",
        1 => "1 plugin",
        int n => $"{n} plugins in chain",
    };

    /// <summary>Label for a compact button that opens the chain window.</summary>
    public string ButtonText => Items.Count == 0 ? "Inserts…" : $"Inserts ({Items.Count})…";

    private PluginChoice? _selectedPlugin;
    public PluginChoice? SelectedPlugin
    {
        get => _selectedPlugin;
        set { if (Set(ref _selectedPlugin, value)) Raise(nameof(CanAdd)); }
    }

    public bool CanAdd => _selectedPlugin is not null;

    private string? _selectedChainPreset;
    public string? SelectedChainPreset
    {
        get => _selectedChainPreset;
        set { if (Set(ref _selectedChainPreset, value)) Raise(nameof(CanUseChainPreset)); }
    }
    public bool CanUseChainPreset => SelectedChainPreset is not null;

    private string? _newPresetName;
    public string? NewPresetName { get => _newPresetName; set => Set(ref _newPresetName, value); }

    private string? _presetNote;
    public string? PresetNote { get => _presetNote; private set => Set(ref _presetNote, value); }
    private bool _presetRequestRunning;

    private string? _note;
    /// <summary>Picker status: scanning, count, or why nothing is offered.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    // One catalog fetch per daemon connection, shared by every chain (the
    // XLR strips and all the mixes), so the controls windows can build their
    // sliders from restored state without anyone opening a picker first.
    private static Task<JsonNode?>? _catalogTask;

    private static Task<JsonNode?> CatalogAsync(DaemonClient client)
        => _catalogTask ??= client.RequestPluginsAsync(TimeSpan.FromSeconds(20));

    /// <summary>Fetch the catalog once per connection (lilv's scan can take a moment).</summary>
    public async void EnsurePluginsLoaded()
    {
        if (_pluginsRequested) return;
        _pluginsRequested = true;
        Note = "Loading isolated LV2 / VST3 catalogue…";
        JsonNode? plugins = await CatalogAsync(_client);
        Dispatcher.UIThread.Post(() =>
        {
            PluginChoices.Clear();
            if (plugins is not JsonArray arr) { Note = "Plugin list unavailable"; _pluginsRequested = false; _catalogTask = null; return; }
            foreach (JsonNode? p in arr)
            {
                if (p is null) continue;
                if (p["scanStatus"]?.GetValue<string>() is string status && status != "ready") continue;
                // Mono chains take mono in / mono out plugins; stereo chains take
                // plugins with at least two ins and two outs (extra ports stay unlinked).
                int ins = p["audioIns"]?.GetValue<int>() ?? 0, outs = p["audioOuts"]?.GetValue<int>() ?? 0;
                bool fits = _channels == 1 ? ins == 1 && outs == 1 : ins >= 2 && outs >= 2;
                if (!fits) continue;
                PluginChoices.Add(new PluginChoice(
                    p["plugin"]!.GetValue<string>(),
                    p["name"]?.GetValue<string>() ?? p["plugin"]!.GetValue<string>(),
                    p["category"]?.GetValue<string>() ?? "",
                    p["params"] ?? new JsonArray(), p["hasNativeUi"]?.GetValue<bool>() ?? false,
                    p["kind"]?.GetValue<string>() ?? "lv2", p["auxiliaryInputs"]));
            }
            _catalogLoaded = true;
            foreach (InsertViewModel insert in Items) insert.RefreshCatalog();
            string width = _channels == 1 ? "mono" : "stereo";
            Note = PluginChoices.Count == 0
                ? $"No ready {width} LV2/VST3 plugins found"
                : $"{PluginChoices.Count} {width} LV2/VST3 plugins available";
        });
    }

    public void ResetForNewConnection()
    {
        InvalidateCatalog();
        _presetRequestRunning = false;
        NativeUiSupported = false;
        ChainPresetNames.Clear();
        PluginPresetNames.Clear();
    }

    internal void InvalidateCatalog()
    {
        _pluginsRequested = false;
        _catalogLoaded = false;
        _catalogTask = null;
    }

    public async Task RefreshPresetsAsync()
    {
        if (_presetRequestRunning) return;
        _presetRequestRunning = true;
        JsonNode? response = await _client.RequestPresetsAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ChainPresetNames.Clear();
            PluginPresetNames.Clear();
            if (response?["chains"] is JsonArray chains)
                foreach (JsonNode? name in chains)
                    if (name is not null) ChainPresetNames.Add(name.GetValue<string>());
            if (response?["plugins"] is JsonArray plugins)
                foreach (JsonNode? name in plugins)
                    if (name is not null) PluginPresetNames.Add(name.GetValue<string>());
            PresetNote = response is null ? "Preset list unavailable" : null;
            _presetRequestRunning = false;
        });
    }

    public async Task SaveChainPresetAsync()
    {
        string name = NewPresetName?.Trim() ?? "";
        if (name.Length == 0) { PresetNote = "Enter a preset name first."; return; }
        PresetNote = await _client.SaveChainPresetAsync(_channel, name);
        if (PresetNote is null) { NewPresetName = ""; await RefreshPresetsAsync(); }
    }

    public async Task LoadChainPresetAsync()
    {
        if (SelectedChainPreset is null) return;
        PresetNote = await _client.LoadChainPresetAsync(_channel, SelectedChainPreset);
    }

    public async Task DeleteChainPresetAsync()
    {
        if (SelectedChainPreset is not string name) return;
        PresetNote = await _client.DeletePresetAsync("chain", name);
        if (PresetNote is null) { SelectedChainPreset = null; await RefreshPresetsAsync(); }
    }

    /// <summary>Whether the catalog has arrived for this chain.</summary>
    public bool CatalogReady => _catalogLoaded || PluginChoices.Count > 0 && !_pluginsRequested;

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
                    vm = new InsertViewModel(this, id, ins["kind"]?.GetValue<string>() ?? "lv2",
                        ins["plugin"]!.GetValue<string>(), ins["label"]?.GetValue<string>() ?? id);
                vm.ApplyFromDaemon(ins, entry?["error"]?.GetValue<string>(),
                    entry?["status"]?.GetValue<string>() ?? "ready");
                vm.RefreshCatalog();
                next.Add(vm);
            }
            if (!next.SequenceEqual(Items))
            {
                Items.Clear();
                foreach (InsertViewModel vm in next) Items.Add(vm);
                Raise(nameof(HasItems));
                Raise(nameof(Summary));
                Raise(nameof(ButtonText));
            }
        }
        finally { _applying = false; }
    }

    // --- edits, all expressed as a new whole chain ---

    public void Add() { if (_selectedPlugin is not null) Add(_selectedPlugin); }

    public void Add(PluginChoice plugin)
    {
        var chain = Snapshot();
        chain.Add(new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["kind"] = plugin.Kind,
            ["plugin"] = plugin.Uri,
            ["label"] = plugin.Name,
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
        string key = ParamSyncKey(item.Id, symbol);
        SliderSync.Touch(key);
        SliderSync.Send(key, () => _ = _client.SetInsertParamAsync(_channel, item.Id, symbol, value));
    }

    internal bool Applying => _applying;
    internal string ParamSyncKey(string id, string symbol) => $"ins:{_channel}:{id}:{symbol}";

    internal Task<string?> ShowNativeUiAsync(string insertId) => _client.ShowInsertUiAsync(_channel, insertId);

    /// <summary>The current chain as the daemon wants it, minus an optional id.</summary>
    private List<object> Snapshot(IEnumerable<InsertViewModel>? order = null, string? skip = null)
        => [.. (order ?? Items).Where(i => i.Id != skip).Select(i => (object)i.ToPayload())];

    /// <summary>Parameter metadata for a plugin uri, from the catalog.</summary>
    internal JsonNode? ParamsFor(string kind, string uri)
        => PluginChoices.FirstOrDefault(p => p.Kind == kind && p.Uri == uri)?.Params;

    internal JsonNode? AuxiliaryInputsFor(string kind, string uri)
        => PluginChoices.FirstOrDefault(p => p.Kind == kind && p.Uri == uri)?.AuxiliaryInputs;

    public void SetSidechainSources(JsonNode? sources)
    {
        SidechainSources.Clear();
        SidechainSources.Add(new SidechainSourceChoice(null, "Off", 0));
        if (sources is JsonArray array)
            foreach (JsonNode? source in array)
                if (source is not null && (source["available"]?.GetValue<bool>() ?? true))
                    SidechainSources.Add(new SidechainSourceChoice(
                        source["id"]!.GetValue<string>(),
                        source["name"]?.GetValue<string>() ?? source["id"]!.GetValue<string>(),
                        source["channels"]?.GetValue<int>() ?? 0));
        foreach (InsertViewModel insert in Items) insert.RefreshSidechains();
    }

    internal IReadOnlyList<SidechainSourceChoice> CompatibleSidechainSources(int busChannels)
        => [.. SidechainSources.Where(source => source.Id is null ||
            source.Channels <= busChannels &&
            source.Id != (_channel.StartsWith("mix:", StringComparison.Ordinal) ? _channel : $"channel:{_channel}") &&
            (_channel.StartsWith("mix:", StringComparison.Ordinal) || !source.Id.StartsWith("mix:", StringComparison.Ordinal)))];

    internal void SendSidechain()
    {
        if (!_applying) _ = _client.SetInsertsAsync(_channel, Snapshot());
    }

    internal Task<string?> RetryAsync(string insertId, bool clearQuarantine)
        => _client.RetryInsertHostAsync(_channel, insertId, clearQuarantine);

    internal Task<string?> SavePluginPresetAsync(string insertId, string name)
        => _client.SavePluginPresetAsync(_channel, insertId, name);

    internal Task<string?> LoadPluginPresetAsync(string insertId, string name)
        => _client.LoadPluginPresetAsync(_channel, insertId, name);
}

public sealed class InsertViewModel : ViewModelBase
{
    private readonly InsertsViewModel _owner;

    public InsertViewModel(InsertsViewModel owner, string id, string kind, string plugin, string label)
    {
        _owner = owner;
        Id = id;
        Kind = kind;
        Plugin = plugin;
        Label = label;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Plugin { get; }
    public string Label { get; }

    private string SearchText => $"{Label} {Plugin}".ToLowerInvariant();
    public bool IsEqualizer => SearchText.Contains("equaliz") || SearchText.Contains(" eq") || SearchText.Contains("filter");
    public bool IsDynamics => SearchText.Contains("compress") || SearchText.Contains("limit") ||
                              SearchText.Contains("dynamic") || SearchText.Contains("gate") || SearchText.Contains("expander");
    public bool HasVisualization => IsEqualizer || IsDynamics;

    /// <summary>The channel chain this insert belongs to (row buttons route through it).</summary>
    public InsertsViewModel Owner => _owner;

    private readonly Dictionary<string, double> _params = [];
    private readonly Dictionary<string, string> _sidechains = [];
    private string? _state;
    private string _status = "ready";

    private bool _bypass;
    public bool Bypass
    {
        get => _bypass;
        set { if (Set(ref _bypass, value)) { Raise(nameof(StateText)); Raise(nameof(IsActive)); _owner.SendBypass(this, value); } }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(StateText)); Raise(nameof(IsActive)); } }
    }
    public bool HasError => _error is not null;

    public string StateText => Bypass ? "bypassed" : _status switch
    {
        "quarantined" => "quarantined",
        "recovering" => "retry pending",
        _ => HasError ? "problem" : "active",
    };

    /// <summary>Green LED: in the chain and processing. Red otherwise (bypassed or failed).</summary>
    public bool IsActive => !Bypass && !HasError;
    public bool CanRetry => !Bypass && _status == "recovering";
    public bool CanUnquarantine => !Bypass && _status == "quarantined";
    public bool CanOpenNativeUi => !OpeningNativeUi && IsActive && _owner.NativeUiSupported
        && _owner.PluginChoices.Any(p => p.Uri == Plugin && p.HasNativeUi);

    public ObservableCollection<InsertParamViewModel> Params { get; } = [];
    public ObservableCollection<InsertSidechainViewModel> Sidechains { get; } = [];
    public bool HasSidechains => Sidechains.Count > 0;
    public ObservableCollection<string> PluginPresetNames => _owner.PluginPresetNames;

    private string? _selectedPluginPreset;
    public string? SelectedPluginPreset
    {
        get => _selectedPluginPreset;
        set { if (Set(ref _selectedPluginPreset, value)) Raise(nameof(CanLoadPluginPreset)); }
    }
    public bool CanLoadPluginPreset => SelectedPluginPreset is not null;

    private string? _newPluginPresetName;
    public string? NewPluginPresetName
    {
        get => _newPluginPresetName;
        set => Set(ref _newPluginPresetName, value);
    }

    private string? _presetNote;
    public string? PresetNote { get => _presetNote; private set => Set(ref _presetNote, value); }

    private bool _openingNativeUi;
    public bool OpeningNativeUi { get => _openingNativeUi; private set { Set(ref _openingNativeUi, value); Raise(nameof(CanOpenNativeUi)); } }
    private string? _nativeUiNote;
    public string? NativeUiNote { get => _nativeUiNote; private set => Set(ref _nativeUiNote, value); }

    public async Task OpenNativeUiAsync()
    {
        if (OpeningNativeUi) return;
        OpeningNativeUi = true;
        NativeUiNote = "Opening the audio-processing plugin's editor…";
        try { NativeUiNote = await _owner.ShowNativeUiAsync(Id); }
        finally { OpeningNativeUi = false; }
    }

    public Task<string?> RetryAsync(bool clearQuarantine)
        => _owner.RetryAsync(Id, clearQuarantine);

    public async Task SavePluginPresetAsync()
    {
        string name = NewPluginPresetName?.Trim() ?? "";
        if (name.Length == 0) { PresetNote = "Enter a preset name first."; return; }
        PresetNote = await _owner.SavePluginPresetAsync(Id, name);
        if (PresetNote is null)
        {
            NewPluginPresetName = "";
            await _owner.RefreshPresetsAsync();
        }
    }

    public async Task LoadPluginPresetAsync()
    {
        if (SelectedPluginPreset is null) return;
        PresetNote = await _owner.LoadPluginPresetAsync(Id, SelectedPluginPreset);
    }

    /// <summary>
    /// Controls grouped for the window. LV2 has no control-port grouping in
    /// practice (LSP groups only its audio ports), so groups come from a
    /// name heuristic; a plugin with few controls stays one flat list.
    /// </summary>
    public ObservableCollection<InsertParamGroup> Groups { get; } = [];

    /// <summary>
    /// Build the control view models on first use (the controls window
    /// opening). If the catalog is not here yet, ask for it and build as
    /// soon as it lands.
    /// </summary>
    public void EnsureParams()
    {
        if (Params.Count > 0) return;
        if (_owner.CatalogReady) { BuildParams(); return; }
        _owner.EnsurePluginsLoaded();
    }

    internal void RefreshCatalog()
    {
        if (_owner.CatalogReady && Params.Count == 0) BuildParams();
        RefreshSidechains();
        Raise(nameof(CanOpenNativeUi));
    }

    internal void RefreshSidechains()
    {
        if (_owner.AuxiliaryInputsFor(Kind, Plugin) is not JsonArray buses)
        {
            if (Sidechains.Count > 0) { Sidechains.Clear(); Raise(nameof(HasSidechains)); }
            return;
        }
        var existing = Sidechains.ToDictionary(sidechain => sidechain.BusId, StringComparer.Ordinal);
        var next = new List<InsertSidechainViewModel>();
        foreach (JsonNode? bus in buses)
        {
            if (bus is null) continue;
            string id = bus["id"]!.GetValue<string>();
            int channels = bus["channels"]?.GetValue<int>() ?? 0;
            if (!existing.TryGetValue(id, out InsertSidechainViewModel? viewModel))
                viewModel = new InsertSidechainViewModel(this, id,
                    bus["name"]?.GetValue<string>() ?? id, channels);
            viewModel.UpdateChoices(_owner.CompatibleSidechainSources(channels),
                _sidechains.GetValueOrDefault(id));
            next.Add(viewModel);
        }
        if (!next.SequenceEqual(Sidechains))
        {
            Sidechains.Clear();
            foreach (InsertSidechainViewModel sidechain in next) Sidechains.Add(sidechain);
            Raise(nameof(HasSidechains));
        }
    }

    private static readonly (string Group, string[] Keys)[] GroupRules =
    [
        ("Display",   ["show", "overlay", "visib", "meter", "graph", "pause", "clear", "zoom", " ui", "display"]),
        ("Sidechain", ["sidechain", "link", "listen"]),
        ("Filter",    ["filter", "frequency", "-pass", "cutoff", "eq ", "equaliz", "band"]),
        ("Dynamics",  ["attack", "release", "threshold", "ratio", "knee", "hold", "hysteresis", "curve", "zone",
                       "reduction", "boost", "compress", "expan", "gate", "limit", "envelope", "lookahead"]),
        ("Levels",    ["gain", "level", "makeup", "dry", "wet", "balance", "mix", "volume", "preamp", "trim", "pan"]),
    ];

    private static readonly string[] GroupOrder = ["General", "Levels", "Dynamics", "Sidechain", "Filter", "Display"];

    private static string GroupFor(string name)
    {
        string n = " " + name.ToLowerInvariant();
        foreach ((string group, string[] keys) in GroupRules)
            if (keys.Any(k => n.Contains(k, StringComparison.Ordinal))) return group;
        return "General";
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        if (Params.Count <= 12)
        {
            Groups.Add(new InsertParamGroup("", false, [.. Params]));
            return;
        }
        var buckets = new Dictionary<string, List<InsertParamViewModel>>();
        foreach (InsertParamViewModel p in Params)
        {
            string g = GroupFor(p.Name);
            if (!buckets.TryGetValue(g, out List<InsertParamViewModel>? l)) buckets[g] = l = [];
            l.Add(p);
        }
        foreach (string g in GroupOrder)
            if (buckets.TryGetValue(g, out List<InsertParamViewModel>? l))
                Groups.Add(new InsertParamGroup(g, true, l));
    }

    public void ApplyFromDaemon(JsonNode ins, string? error, string status)
    {
        _bypass = ins["bypass"]?.GetValue<bool>() ?? false;
        _status = status;
        Raise(nameof(Bypass));
        Raise(nameof(StateText));
        Raise(nameof(IsActive));
        Raise(nameof(CanRetry));
        Raise(nameof(CanUnquarantine));
        Error = error;
        _state = ins["state"]?.GetValue<string>();
        _sidechains.Clear();
        if (ins["sidechains"] is JsonObject sidechains)
            foreach ((string bus, JsonNode? source) in sidechains)
                if (source is not null) _sidechains[bus] = source.GetValue<string>();
        RefreshSidechains();
        _params.Clear();
        if (ins["params"] is JsonObject po)
            foreach ((string k, JsonNode? v) in po)
                if (v is not null) _params[k] = v.GetValue<double>();
        foreach (InsertParamViewModel p in Params)
        {
            // While a control is being dragged the daemon's echo lags the
            // slider; applying it would make the thumb jitter (the mixer's
            // faders use the same guard).
            if (SliderSync.RecentlyTouched(_owner.ParamSyncKey(Id, p.Symbol))) continue;
            if (_params.TryGetValue(p.Symbol, out double v)) p.ApplyFromDaemon(v);
        }
    }

    /// <summary>Put every control back to the plugin's declared default, live.</summary>
    public void ResetToDefaults()
    {
        EnsureParams();
        foreach (InsertParamViewModel p in Params) p.Value = p.Default;
    }

    private void BuildParams()
    {
        if (_owner.ParamsFor(Kind, Plugin) is not JsonArray arr) return;
        foreach (JsonNode? p in arr)
        {
            if (p is null) continue;
            string sym = p["symbol"]!.GetValue<string>();
            var points = new List<InsertScalePoint>();
            if (p["scalePoints"] is JsonArray pointArray)
                foreach (JsonNode? point in pointArray)
                    if (point is not null)
                        points.Add(new InsertScalePoint(point["label"]?.GetValue<string>() ?? "",
                            point["value"]?.GetValue<double>() ?? 0));
            var vm = new InsertParamViewModel(this, sym, p["name"]?.GetValue<string>() ?? sym,
                p["min"]?.GetValue<double>() ?? 0, p["max"]?.GetValue<double>() ?? 1,
                p["default"]?.GetValue<double>() ?? 0,
                p["toggled"]?.GetValue<bool>() ?? false, p["integer"]?.GetValue<bool>() ?? false,
                p["logarithmic"]?.GetValue<bool>() ?? false,
                p["enumeration"]?.GetValue<bool>() ?? false, points,
                p["unit"]?.GetValue<string>() ?? "");
            if (_params.TryGetValue(sym, out double cur)) vm.ApplyFromDaemon(cur);
            Params.Add(vm);
        }
        RebuildGroups();
    }

    internal void SendParam(string symbol, double value)
    {
        _params[symbol] = value;
        _owner.SendParam(this, symbol, value);
    }

    internal void SetSidechain(string busId, string? sourceId)
    {
        if (sourceId is null) _sidechains.Remove(busId);
        else _sidechains[busId] = sourceId;
        _owner.SendSidechain();
    }

    internal object ToPayload() => new Dictionary<string, object?>
    {
        ["id"] = Id,
        ["kind"] = Kind,
        ["plugin"] = Plugin,
        ["label"] = Label,
        ["bypass"] = _bypass,
        ["params"] = new Dictionary<string, double>(_params),
        ["state"] = _state,
        ["sidechains"] = new Dictionary<string, string>(_sidechains),
    };
}

public sealed record SidechainSourceChoice(string? Id, string Name, int Channels)
{
    public override string ToString() => Id is null ? Name : $"{Name} ({Channels} ch)";
}

/// <summary>One real auxiliary input bus and its selected stable signal source.</summary>
public sealed class InsertSidechainViewModel : ViewModelBase
{
    private readonly InsertViewModel _insert;
    private bool _applying;

    public InsertSidechainViewModel(InsertViewModel insert, string busId, string name, int channels)
    {
        _insert = insert;
        BusId = busId;
        Name = name;
        Channels = channels;
    }

    public string BusId { get; }
    public string Name { get; }
    public int Channels { get; }
    public ObservableCollection<SidechainSourceChoice> Sources { get; } = [];

    private SidechainSourceChoice? _selected;
    public SidechainSourceChoice? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && !_applying)
                _insert.SetSidechain(BusId, value?.Id);
        }
    }

    internal void UpdateChoices(IReadOnlyList<SidechainSourceChoice> sources, string? selectedId)
    {
        _applying = true;
        try
        {
            Sources.Clear();
            foreach (SidechainSourceChoice source in sources) Sources.Add(source);
            Selected = Sources.FirstOrDefault(source => source.Id == selectedId) ?? Sources.FirstOrDefault();
        }
        finally { _applying = false; }
    }
}

/// <summary>A titled run of controls in the controls window.</summary>
public sealed record InsertParamGroup(string Name, bool ShowHeader, IReadOnlyList<InsertParamViewModel> Params);

public sealed record InsertScalePoint(string Label, double Value)
{
    public override string ToString() => Label;
}

/// <summary>One control port as a slider or switch.</summary>
public sealed class InsertParamViewModel : ViewModelBase
{
    private readonly InsertViewModel _owner;
    private bool _applying;

    public InsertParamViewModel(InsertViewModel owner, string symbol, string name,
        double min, double max, double def, bool toggled, bool integer, bool logarithmic,
        bool enumeration, IReadOnlyList<InsertScalePoint> scalePoints, string unit)
    {
        _owner = owner;
        Symbol = symbol;
        Name = name;
        Min = min;
        Max = max;
        Toggled = toggled;
        Integer = integer;
        Logarithmic = logarithmic;
        Enumeration = enumeration && scalePoints.Count > 0;
        ScalePoints = scalePoints;
        RawUnit = unit;
        Default = def;
        _value = def;
    }

    public string Symbol { get; }
    public string Name { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public bool Toggled { get; }
    public bool Integer { get; }
    public bool Logarithmic { get; }
    public bool Enumeration { get; }
    public IReadOnlyList<InsertScalePoint> ScalePoints { get; }
    public string RawUnit { get; }
    public bool IsKnob => !Toggled && !Enumeration;
    public double Step => Integer ? 1 : 0;

    private string LowerName => Name.ToLowerInvariant();
    public string Unit
    {
        get
        {
            string declared = RawUnit.ToLowerInvariant() switch
            {
                "hz" => "Hz",
                "khz" => "kHz",
                "mhz" => "MHz",
                "ms" => "ms",
                "s" => "s",
                "db" => "dB",
                "degree" => "°",
                "pc" or "percent" => "%",
                "bpm" => "BPM",
                "oct" => "oct",
                "semitone12tet" => "st",
                "gain" => "dB",
                _ => "",
            };
            if (declared.Length > 0) return declared;
            return LowerName.Contains("frequency") || LowerName.Contains("cutoff") ? "Hz"
                : LowerName.Contains("attack") || LowerName.Contains("release") ||
                  LowerName.Contains("lookahead") || LowerName.Contains("hold time") ? "ms"
                : LowerName.Contains("ratio") ? ":1"
                : IsLinearGain ? "dB"
                : "";
        }
    }
    private bool IsLinearGain => RawUnit.Equals("gain", StringComparison.OrdinalIgnoreCase) || Logarithmic && Min >= 0 &&
        (LowerName.Contains("gain") || LowerName.Contains("threshold") || LowerName.Contains("level") || LowerName.Contains("knee"));
    internal double Decibels => IsLinearGain ? 20 * Math.Log10(Math.Max(Value, 0.000001)) : Value;

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Clamp(Integer ? Math.Round(value) : value, Min, Max);
            if (!Set(ref _value, value)) return;
            Raise(nameof(ValueText));
            Raise(nameof(On));
            Raise(nameof(SelectedScalePoint));
            if (!_applying) _owner.SendParam(Symbol, value);
        }
    }

    /// <summary>Toggle view of the value for switch ports.</summary>
    public bool On
    {
        get => _value >= 0.5;
        set => Value = value ? 1 : 0;
    }

    public InsertScalePoint? SelectedScalePoint
    {
        get => ScalePoints.OrderBy(p => Math.Abs(p.Value - _value)).FirstOrDefault();
        set { if (value is not null) Value = value.Value; }
    }

    public string ValueText => Toggled ? (On ? "on" : "off")
        : Enumeration ? SelectedScalePoint?.Label ?? _value.ToString("0.###")
        : IsLinearGain ? (_value == 0 ? "−∞ dB" : $"{Decibels:0.0} dB")
        : Unit == "Hz" && Math.Abs(_value) >= 1000 ? $"{_value / 1000:0.##} kHz"
        : Unit.Length > 0 ? $"{Format(_value)} {Unit}"
        : Integer ? ((int)Math.Round(_value)).ToString()
        : Format(_value);

    private static string Format(double value) => Math.Abs(value) >= 100 ? value.ToString("0")
        : Math.Abs(value) >= 10 ? value.ToString("0.0")
        : value.ToString("0.###");

    public void ApplyFromDaemon(double v)
    {
        _applying = true;
        try { Value = v; }
        finally { _applying = false; }
    }
}
