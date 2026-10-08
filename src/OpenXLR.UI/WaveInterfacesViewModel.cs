using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public sealed partial class MainViewModel
{
    public ObservableCollection<WaveInterfaceViewModel> WaveInterfaces { get; } = [];
    private void ApplyWaveInterfaces(JsonNode? value)
    {
        if (value is not JsonArray interfaces) { foreach (var model in WaveInterfaces) model.Retire(); WaveInterfaces.Clear(); return; }
        SyncList(WaveInterfaces, interfaces, node => node["id"]!.GetValue<string>(),
            (node, model) => model.Apply(node), node => new WaveInterfaceViewModel(_client, this, node["id"]!.GetValue<string>()), model => model.Retire());
    }
}

public sealed class WaveInterfaceViewModel(DaemonClient client, MainViewModel main, string id) : ViewModelBase, IHasId
{
    public string Id { get; } = id;
    private bool _applying;
    private bool _busy;
    private bool _retired;
    private int _epoch;
    public string Name { get; private set; } = "";
    public bool Active { get; private set; }
    public bool Connected { get; private set; }
    public bool CanEnable => !_retired && !Active && !_busy;
    public bool CanControl => !_retired && Connected && !_busy;
    public bool HasGain { get; private set; }
    public bool HasMute { get; private set; }
    public bool HasPhantom { get; private set; }
    public bool HasLowCut { get; private set; }
    public bool HasClipGuard { get; private set; }
    public bool GainLocked { get; private set; }
    public bool CanAdjustGain => CanControl && !GainLocked;
    public bool HasSecondInput { get; private set; }
    public bool PhantomSettling { get; private set; }
    public string? Error { get; private set; }
    public string? Warning { get; private set; }
    public ObservableCollection<AudioDeviceItem> Sources { get; } = [];
    public AudioDeviceItem? Source { get; set; }
    public string ChannelName { get; set; } = "";
    public int InputNumber { get; set; } = 1;
    public int MaximumInput => HasSecondInput ? 2 : 1;
    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_applying || _enabled == value || !CanEnable) return;
            _ = EnableAsync(value);
        }
    }
    private int _gain, _gain2;
    public int Gain { get => _gain; set { if ((_applying || CanAdjustGain) && Set(ref _gain, value) && !_applying) QueueGain("gain", value); } }
    public int Gain2 { get => _gain2; set { if ((_applying || CanAdjustGain) && Set(ref _gain2, value) && !_applying) QueueGain("gain2", value); } }
    private bool _mute, _mute2, _phantom, _phantom2, _lowCut, _lowCut2, _clipGuard, _clipGuard2;
    public bool Phantom2 { get => _phantom2; set { if ((_applying || CanControl) && Set(ref _phantom2, value) && !_applying) _ = ControlAsync("phantom2", value); } }
    public bool LowCut { get => _lowCut; set { if ((_applying || CanControl) && Set(ref _lowCut, value) && !_applying) _ = ControlAsync("lowCut", value); } }
    public bool LowCut2 { get => _lowCut2; set { if ((_applying || CanControl) && Set(ref _lowCut2, value) && !_applying) _ = ControlAsync("lowCut2", value); } }
    public bool ClipGuard { get => _clipGuard; set { if ((_applying || CanControl) && Set(ref _clipGuard, value) && !_applying) _ = ControlAsync("clipGuard", value); } }
    public bool ClipGuard2 { get => _clipGuard2; set { if ((_applying || CanControl) && Set(ref _clipGuard2, value) && !_applying) _ = ControlAsync("clipGuard2", value); } }
    public bool Mute { get => _mute; set { if ((_applying || CanControl) && Set(ref _mute, value) && !_applying) _ = ControlAsync("mute", value); } }
    public bool Mute2 { get => _mute2; set { if ((_applying || CanControl) && Set(ref _mute2, value) && !_applying) _ = ControlAsync("mute2", value); } }
    public bool Phantom { get => _phantom; set { if ((_applying || CanControl) && Set(ref _phantom, value) && !_applying) _ = ControlAsync("phantom", value); } }
    private void QueueGain(string control, int value)
    {
        string key = $"wave:{Id}:{control}";
        int epoch = _epoch;
        SliderSync.Touch(key); SliderSync.Send(key, () => { if (epoch == _epoch && !_retired) _ = ControlAsync(control, value); });
    }
    internal async Task EnableAsync(bool value)
    {
        if (!CanEnable) return;
        int epoch = _epoch;
        _busy = true; Raise(null);
        try
        {
            string? error = await client.SetWaveInterfaceEnabledAsync(Id, value);
            if (_retired || epoch != _epoch) return;
            Error = error;
            if (Error is null) _enabled = value;
        }
        finally { if (!_retired && epoch == _epoch) { _busy = false; Raise(null); } }
    }
    private async Task ControlAsync(string control, object value)
    {
        if (_retired) return;
        if (!Connected) { Error = "The Wave interface is disconnected."; Raise(nameof(Error)); return; }
        int epoch = _epoch;
        string? error = await client.SetWaveControlAsync(Id, control, value);
        if (!_retired && epoch == _epoch) { Error = error; Raise(nameof(Error)); }
    }
    public async Task AddInputAsync()
    {
        if (!CanControl) return;
        if (!Connected || Source is null) { Error = "Enable the interface and choose its available capture source first."; Raise(nameof(Error)); return; }
        int channel = Math.Clamp(InputNumber, 1, MaximumInput);
        string name = string.IsNullOrWhiteSpace(ChannelName) ? $"{Name} input {channel}" : ChannelName.Trim();
        if (name.Length > 60) name = name[..60];
        int epoch = _epoch;
        _busy = true; Raise(null);
        try
        {
            string? error = await client.CreateCaptureChannelAsync(name, Source.Name, 0, (channel - 1) * 2);
            if (!_retired && epoch == _epoch) Error = error;
        }
        finally { if (!_retired && epoch == _epoch) { _busy = false; Raise(null); } }
    }
    internal void ResetConnection()
    {
        _epoch++; _busy = false; Connected = false;
        SliderSync.Forget($"wave:{Id}:gain"); SliderSync.Forget($"wave:{Id}:gain2");
        Raise(null);
    }
    internal void Retire() { ResetConnection(); _retired = true; Raise(null); }
    internal void Apply(JsonNode value)
    {
        if (_retired) return;
        if (Connected && value["connected"]?.GetValue<bool>() != true) ResetConnection();
        _applying = true;
        try
        {
            Name = value["name"]?.GetValue<string>() ?? Id;
            Active = value["active"]?.GetValue<bool>() ?? false;
            Connected = value["connected"]?.GetValue<bool>() ?? false;
            _enabled = value["enabled"]?.GetValue<bool>() ?? false;
            Warning = value["warning"]?.GetValue<string>();
            var capabilities = value["capabilities"];
            HasGain = capabilities?["gain"]?.GetValue<bool>() ?? false;
            HasMute = capabilities?["mute"]?.GetValue<bool>() ?? false;
            HasPhantom = capabilities?["phantom"]?.GetValue<bool>() ?? false;
            HasLowCut = capabilities?["lowCut"]?.GetValue<bool>() ?? false;
            HasClipGuard = capabilities?["clipGuard"]?.GetValue<bool>() ?? false;
            HasSecondInput = (capabilities?["xlrInputs"]?.GetValue<int>() ?? 1) > 1;
            var state = value["state"];
            if (!SliderSync.RecentlyTouched($"wave:{Id}:gain")) Gain = state?["gainDb"]?.GetValue<int>() ?? 0;
            if (!SliderSync.RecentlyTouched($"wave:{Id}:gain2")) Gain2 = state?["gain2Db"]?.GetValue<int>() ?? 0;
            GainLocked = state?["gainLocked"]?.GetValue<bool>() ?? false;
            LowCut = state?["lowCut"]?.GetValue<bool>() ?? false;
            LowCut2 = state?["lowCut2"]?.GetValue<bool>() ?? false;
            ClipGuard = state?["clipGuard"]?.GetValue<bool>() ?? false;
            ClipGuard2 = state?["clipGuard2"]?.GetValue<bool>() ?? false;
            Phantom2 = state?["phantom2"]?.GetValue<bool>() ?? false;
            Mute = state?["mute"]?.GetValue<bool>() ?? false;
            Mute2 = state?["mute2"]?.GetValue<bool>() ?? false;
            Phantom = state?["phantom"]?.GetValue<bool>() ?? false;
            PhantomSettling = state?["phantomSettling"]?.GetValue<bool>() ?? false;
            string hint = value["captureHint"]?.GetValue<string>() ?? "";
            var choices = main.Inputs.Where(source => hint.Length > 0 && !source.IsOwn && source.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Preserve an open picker until actual membership changes. No
            // ambiguous source is automatically chosen for a duplicate model.
            if (!choices.Select(source => source.Name).SequenceEqual(Sources.Select(source => source.Name)))
            {
                string? selected = Source?.Name; Sources.Clear(); foreach (var source in choices) Sources.Add(source);
                Source = Sources.FirstOrDefault(source => source.Name == selected) ?? (Sources.Count == 1 ? Sources[0] : null);
            }
        }
        finally { _applying = false; Raise(null); }
    }
}
