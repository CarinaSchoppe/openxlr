using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace OpenXLR.UI;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Set a field and notify; returns false if unchanged.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// Root view model. Applies daemon state pushes to the bound properties and
/// sends user changes back. A guard flag suppresses echo: while applying a push
/// we must not re-send the values we just received.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private bool _applying;

    public MainViewModel(DaemonClient client)
    {
        _client = client;
        _client.StateReceived += node => Dispatcher.UIThread.Post(() => Apply(node));
        _client.ConnectionChanged += up => Dispatcher.UIThread.Post(() =>
        {
            DaemonConnected = up;
            if (!up) { DeviceConnected = false; Status = "daemon not running"; }
        });
        _client.ErrorReceived += msg => Dispatcher.UIThread.Post(() => Status = msg);
        _client.MetersReceived += levels => Dispatcher.UIThread.Post(() => ApplyMeters(levels));
    }

    // --- connection / device identity ---

    private bool _daemonConnected;
    public bool DaemonConnected { get => _daemonConnected; private set { if (Set(ref _daemonConnected, value)) Raise(nameof(StatusLine)); } }

    private bool _deviceConnected;
    public bool DeviceConnected { get => _deviceConnected; private set { if (Set(ref _deviceConnected, value)) Raise(nameof(StatusLine)); } }

    private string _deviceName = "none";
    public string DeviceName { get => _deviceName; private set { if (Set(ref _deviceName, value)) Raise(nameof(StatusLine)); } }

    private string _status = "connecting…";
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(StatusLine)); } }

    public string StatusLine => !DaemonConnected ? "Daemon not running"
        : DeviceConnected ? $"{DeviceName} (connected)"
        : "No device";

    // --- hardware controls ---

    private int _gainDb;
    public int GainDb
    {
        get => _gainDb;
        set
        {
            if (Set(ref _gainDb, value) && !_applying)
            {
                int v = value;
                SliderSync.Touch("gain");
                SliderSync.Send("gain", () => _ = _client.SetControlAsync("gain", v));
            }
            Raise(nameof(GainText));
        }
    }
    public string GainText => $"{_gainDb} dB";

    private bool _mute;
    public bool Mute { get => _mute; set { if (Set(ref _mute, value) && !_applying) _ = _client.SetControlAsync("mute", value); } }

    private bool _lowCut;
    public bool LowCut { get => _lowCut; set { if (Set(ref _lowCut, value) && !_applying) _ = _client.SetControlAsync("lowCut", value); } }

    private bool _expander;
    public bool Expander { get => _expander; set { if (Set(ref _expander, value) && !_applying) _ = _client.SetControlAsync("expander", value); } }

    private bool _voiceTune;
    public bool VoiceTune { get => _voiceTune; set { if (Set(ref _voiceTune, value) && !_applying) _ = _client.SetControlAsync("voiceTune", value); } }

    private bool _lowImpedance;
    public bool LowImpedance { get => _lowImpedance; set { if (Set(ref _lowImpedance, value) && !_applying) _ = _client.SetControlAsync("lowImpedance", value); } }

    private double _hpVolumeDb;
    public double HpVolumeDb
    {
        get => _hpVolumeDb;
        set
        {
            if (Set(ref _hpVolumeDb, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch("hp");
                SliderSync.Send("hp", () => _ = _client.SetControlAsync("hpVolumeDb", v));
            }
            Raise(nameof(HpText));
            Raise(nameof(HpPercent));
        }
    }
    public string HpText => $"{HpPercent:0}%";

    /// <summary>
    /// The hardware register is an attenuator in -0.25 dB steps (0 dB = full
    /// output, -60 dB = floor). Shown as 0..100% linear in dB, which is how a
    /// volume knob is expected to feel.
    /// </summary>
    public double HpPercent
    {
        get => (60.0 + _hpVolumeDb) / 60.0 * 100.0;
        set => HpVolumeDb = -60.0 + Math.Clamp(value, 0, 100) * 0.6;
    }

    private double _hp2VolumeDb;
    public double Hp2VolumeDb
    {
        get => _hp2VolumeDb;
        set
        {
            if (Set(ref _hp2VolumeDb, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch("hp2");
                SliderSync.Send("hp2", () => _ = _client.SetControlAsync("hp2VolumeDb", v));
            }
            Raise(nameof(Hp2Text));
            Raise(nameof(Hp2Percent));
        }
    }
    public string Hp2Text => $"{Hp2Percent:0}%";

    public double Hp2Percent
    {
        get => (60.0 + _hp2VolumeDb) / 60.0 * 100.0;
        set => Hp2VolumeDb = -60.0 + Math.Clamp(value, 0, 100) * 0.6;
    }

    // --- second XLR input ---

    private int _gain2Db;
    public int Gain2Db
    {
        get => _gain2Db;
        set
        {
            if (Set(ref _gain2Db, value) && !_applying)
            {
                int v = value;
                SliderSync.Touch("gain2");
                SliderSync.Send("gain2", () => _ = _client.SetControlAsync("gain2", v));
            }
            Raise(nameof(Gain2Text));
        }
    }
    public string Gain2Text => $"{_gain2Db} dB";

    private bool _mute2;
    public bool Mute2 { get => _mute2; set { if (Set(ref _mute2, value) && !_applying) _ = _client.SetControlAsync("mute2", value); } }

    private bool _lowCut2;
    public bool LowCut2 { get => _lowCut2; set { if (Set(ref _lowCut2, value) && !_applying) _ = _client.SetControlAsync("lowCut2", value); } }

    private bool _expander2;
    public bool Expander2 { get => _expander2; set { if (Set(ref _expander2, value) && !_applying) _ = _client.SetControlAsync("expander2", value); } }

    private bool _voiceTune2;
    public bool VoiceTune2 { get => _voiceTune2; set { if (Set(ref _voiceTune2, value) && !_applying) _ = _client.SetControlAsync("voiceTune2", value); } }

    private bool _phantom2;
    public bool Phantom2 { get => _phantom2; set { if (Set(ref _phantom2, value) && !_applying) _ = _client.SetControlAsync("phantom2", value); } }

    private bool _clipGuard2;
    public bool ClipGuard2 { get => _clipGuard2; set { if (Set(ref _clipGuard2, value) && !_applying) _ = _client.SetControlAsync("clipGuard2", value); } }

    private bool _polarity2;
    public bool Polarity2 { get => _polarity2; set { if (Set(ref _polarity2, value) && !_applying) _ = _client.SetControlAsync("polarity2", value); } }

    // Pro-only DSP. Bit assignments are provisional, and these three are exactly
    // what the by-ear labeling pass toggles.
    private bool _phantom;
    public bool Phantom { get => _phantom; set { if (Set(ref _phantom, value) && !_applying) _ = _client.SetControlAsync("phantom", value); } }

    private bool _clipGuard;
    public bool ClipGuard { get => _clipGuard; set { if (Set(ref _clipGuard, value) && !_applying) _ = _client.SetControlAsync("clipGuard", value); } }

    private bool _polarity;
    public bool Polarity { get => _polarity; set { if (Set(ref _polarity, value) && !_applying) _ = _client.SetControlAsync("polarity", value); } }

    // --- mixer ---

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];
    public ObservableCollection<MixViewModel> Mixes { get; } = [];

    // --- device selection (any sink/source, real or virtual) ---

    public ObservableCollection<AudioDeviceItem> Outputs { get; } = [];

    /// <summary>All capture devices, virtual included (for the Options pickers).</summary>
    public ObservableCollection<AudioDeviceItem> Inputs { get; } = [];

    /// <summary>Physical capture devices only: candidates to feed the Mic channel.</summary>
    public ObservableCollection<AudioDeviceItem> MicInputs { get; } = [];

    /// <summary>
    /// Sinks the Monitor mix can play on: everything except OpenXLR's own nodes
    /// (routing the monitor into its own mixer is a feedback loop).
    /// </summary>
    public ObservableCollection<AudioDeviceItem> MonitorOutputs { get; } = [];

    /// <summary>Enforced defaults as reported by the daemon (for the Options window).</summary>
    public string? EnforcedDefaultSink { get; private set; }
    public string? EnforcedDefaultSource { get; private set; }

    /// <summary>Mirrors the ui.json preference; MainWindow consults it on close.</summary>
    public bool MinimizeToTray { get; set; } = UiSettings.Load().MinimizeToTray;

    private AudioDeviceItem? _selectedOutput;
    public AudioDeviceItem? SelectedOutput
    {
        get => _selectedOutput;
        set { if (Set(ref _selectedOutput, value) && !_applying) _ = _client.SetMonitorOutputAsync(value?.Name); }
    }

    private double _outputVolume;
    public double OutputVolume
    {
        get => _outputVolume;
        set
        {
            if (Set(ref _outputVolume, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch("outvol");
                SliderSync.Send("outvol", () => _ = _client.SetOutputVolumeAsync(v));
            }
            Raise(nameof(OutputVolumeText));
        }
    }
    public string OutputVolumeText => $"{_outputVolume * 100:0}%";

    private double _inputVolume;
    public double InputVolume
    {
        get => _inputVolume;
        set
        {
            if (Set(ref _inputVolume, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch("invol");
                SliderSync.Send("invol", () => _ = _client.SetInputVolumeAsync(v));
            }
            Raise(nameof(InputVolumeText));
        }
    }
    public string InputVolumeText => $"{_inputVolume * 100:0}%";

    private AudioDeviceItem? _selectedInput;
    public AudioDeviceItem? SelectedInput
    {
        get => _selectedInput;
        set { if (Set(ref _selectedInput, value) && !_applying) _ = _client.SetMicInputAsync(value?.Name); }
    }

    private bool _hasMixer;
    public bool HasMixer { get => _hasMixer; private set => Set(ref _hasMixer, value); }

    /// <summary>Apply a state push from the daemon without echoing it back.</summary>
    private void Apply(JsonNode node)
    {
        _applying = true;
        try
        {
            DaemonConnected = true;
            DeviceConnected = node["connected"]?.GetValue<bool>() ?? false;
            if (node["device"] is JsonNode dev)
                DeviceName = $"{dev["vendor"]?.GetValue<string>()} {dev["model"]?.GetValue<string>()}".Trim();

            if (node["state"] is JsonNode s)
            {
                if (!SliderSync.RecentlyTouched("gain")) GainDb = s["gainDb"]?.GetValue<int>() ?? 0;
                Mute = s["mute"]?.GetValue<bool>() ?? false;
                LowCut = s["lowCut"]?.GetValue<bool>() ?? false;
                Expander = s["expander"]?.GetValue<bool>() ?? false;
                VoiceTune = s["voiceTune"]?.GetValue<bool>() ?? false;
                LowImpedance = s["lowImpedance"]?.GetValue<bool>() ?? false;
                if (!SliderSync.RecentlyTouched("hp")) HpVolumeDb = s["hpVolumeDb"]?.GetValue<double>() ?? 0;
                if (!SliderSync.RecentlyTouched("hp2")) Hp2VolumeDb = s["hp2VolumeDb"]?.GetValue<double>() ?? 0;
                Phantom = s["phantom"]?.GetValue<bool>() ?? false;
                ClipGuard = s["clipGuard"]?.GetValue<bool>() ?? false;
                Polarity = s["polarity"]?.GetValue<bool>() ?? false;
                if (!SliderSync.RecentlyTouched("gain2")) Gain2Db = s["gain2Db"]?.GetValue<int>() ?? 0;
                Mute2 = s["mute2"]?.GetValue<bool>() ?? false;
                LowCut2 = s["lowCut2"]?.GetValue<bool>() ?? false;
                Expander2 = s["expander2"]?.GetValue<bool>() ?? false;
                VoiceTune2 = s["voiceTune2"]?.GetValue<bool>() ?? false;
                Phantom2 = s["phantom2"]?.GetValue<bool>() ?? false;
                ClipGuard2 = s["clipGuard2"]?.GetValue<bool>() ?? false;
                Polarity2 = s["polarity2"]?.GetValue<bool>() ?? false;
            }

            ApplyDevices(node["devices"], node["mixer"]);
            ApplyMixer(node["mixer"]);
            ApplyStreams(node["mixer"]);
            Status = DeviceConnected ? "ready" : "no device";
        }
        finally { _applying = false; }
    }

    /// <summary>
    /// Refresh the output/input pickers. The lists are rebuilt only when the set
    /// of devices actually changes, so an open dropdown is not disturbed by the
    /// state pushes that arrive on every fader move.
    /// </summary>
    private void ApplyDevices(JsonNode? devices, JsonNode? mixer)
    {
        if (devices is not JsonArray arr) { Outputs.Clear(); Inputs.Clear(); MicInputs.Clear(); return; }

        var sinks = new List<AudioDeviceItem>();
        var sources = new List<AudioDeviceItem>();
        foreach (JsonNode? d in arr)
        {
            if (d is null) continue;
            string? name = d["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            string desc = d["description"]?.GetValue<string>() ?? name;
            bool own = d["isOwn"]?.GetValue<bool>() ?? false;
            bool physical = d["isPhysical"]?.GetValue<bool>() ?? false;
            var item = new AudioDeviceItem(name, desc, own, physical);
            (IsSink(d["kind"]) ? sinks : sources).Add(item);
        }
        Replace(Outputs, sinks);
        Replace(Inputs, sources);
        Replace(MicInputs, [.. sources.Where(s => s.IsPhysical)]);
        Replace(MonitorOutputs, [.. sinks.Where(s => !s.IsOwn)]);

        EnforcedDefaultSink = mixer?["enforcedDefaultSink"]?.GetValue<string>();
        EnforcedDefaultSource = mixer?["enforcedDefaultSource"]?.GetValue<string>();

        if (!SliderSync.RecentlyTouched("outvol"))
            OutputVolume = mixer?["outputVolume"]?.GetValue<double>() ?? 0;
        if (!SliderSync.RecentlyTouched("invol"))
            InputVolume = mixer?["inputVolume"]?.GetValue<double>() ?? 0;

        string? curOut = mixer?["monitorOutput"]?.GetValue<string>();
        string? curIn = mixer?["micInput"]?.GetValue<string>();
        SelectedOutput = MonitorOutputs.FirstOrDefault(o => o.Name == curOut);
        SelectedInput = MicInputs.FirstOrDefault(i => i.Name == curIn);
    }

    /// <summary>AudioNodeKind arrives as the enum's number (0 = Sink) or name.</summary>
    private static bool IsSink(JsonNode? kind) => kind switch
    {
        null => false,
        _ => kind.ToJsonString().Trim('"') is "0" or "Sink",
    };

    private static void Replace(ObservableCollection<AudioDeviceItem> target, List<AudioDeviceItem> fresh)
    {
        if (target.Count == fresh.Count && target.Select(x => x.Name).SequenceEqual(fresh.Select(x => x.Name)))
            return;   // unchanged, leave the collection (and any open dropdown) alone
        target.Clear();
        foreach (AudioDeviceItem d in fresh) target.Add(d);
    }

    /// <summary>Route each peak to its channel strip or mix.</summary>
    private void ApplyMeters(JsonNode levels)
    {
        if (levels is not JsonObject obj) return;
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            double l = 0, r = 0;
            if (kv.Value is JsonArray arr && arr.Count >= 2)
            {
                l = arr[0]?.GetValue<double>() ?? 0;
                r = arr[1]?.GetValue<double>() ?? 0;
            }
            if (kv.Key.StartsWith("ch:", StringComparison.Ordinal))
            {
                ChannelViewModel? c = Channels.FirstOrDefault(x => x.Id == kv.Key[3..]);
                if (c is not null) { c.MeterL = l; c.MeterR = r; }
            }
            else if (kv.Key.StartsWith("mix:", StringComparison.Ordinal))
            {
                MixViewModel? m = Mixes.FirstOrDefault(x => x.Id == kv.Key[4..]);
                if (m is not null) { m.MeterL = l; m.MeterR = r; }
            }
        }
    }

    /// <summary>Applications currently playing, with the channel each landed in.</summary>
    public ObservableCollection<AppStreamViewModel> Apps { get; } = [];

    private void ApplyStreams(JsonNode? mixer)
    {
        if (mixer?["streams"] is not JsonArray arr) { Apps.Clear(); return; }
        var fresh = new List<(int Id, string Label, string Channel)>();
        foreach (JsonNode? s in arr)
        {
            if (s is null) continue;
            fresh.Add((s["id"]?.GetValue<int>() ?? 0,
                       s["label"]?.GetValue<string>() ?? "?",
                       s["channelId"]?.GetValue<string>() ?? ""));
        }
        // Update in place so an open dropdown is not closed by a state push.
        foreach (var f in fresh)
        {
            AppStreamViewModel? existing = Apps.FirstOrDefault(a => a.StreamId == f.Id);
            if (existing is null)
                Apps.Add(new AppStreamViewModel(_client, f.Id, f.Label, [.. Channels.Select(c => c.Id)]) { ChannelId = f.Channel });
            else
                existing.ApplyFromDaemon(f.Channel);
        }
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!fresh.Any(f => f.Id == Apps[i].StreamId)) Apps.RemoveAt(i);
    }

    private void ApplyMixer(JsonNode? mixer)
    {
        if (mixer is null) { HasMixer = false; return; }
        HasMixer = true;

        if (mixer["mixes"] is JsonArray mixes)
        {
            SyncList(Mixes, mixes, m => m["id"]!.GetValue<string>(),
                (m, vm) => vm.ApplyFromDaemon(m),
                m => new MixViewModel(_client, m["id"]!.GetValue<string>(), m["name"]!.GetValue<string>()));
        }

        if (mixer["channels"] is JsonArray channels)
        {
            SyncList(Channels, channels, c => c["id"]!.GetValue<string>(),
                (c, vm) => vm.ApplyFromDaemon(c),
                c => new ChannelViewModel(_client, c["id"]!.GetValue<string>(), c["name"]!.GetValue<string>(),
                    [.. Mixes.Select(m => m.Id)]));
        }
    }

    /// <summary>Update in place by id so bindings survive; add/remove as needed.</summary>
    private static void SyncList<T>(ObservableCollection<T> target, JsonArray source,
        Func<JsonNode, string> idOf, Action<JsonNode, T> update, Func<JsonNode, T> create)
        where T : class, IHasId
    {
        var seen = new HashSet<string>();
        foreach (JsonNode? item in source)
        {
            if (item is null) continue;
            string id = idOf(item);
            seen.Add(id);
            T? existing = target.FirstOrDefault(x => x.Id == id);
            if (existing is null) { T made = create(item); update(item, made); target.Add(made); }
            else update(item, existing);
        }
        for (int i = target.Count - 1; i >= 0; i--)
            if (!seen.Contains(target[i].Id)) target.RemoveAt(i);
    }
}

public interface IHasId { string Id { get; } }

/// <summary>An application that is playing, and the channel it is routed to.</summary>
public sealed class AppStreamViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private bool _applying;

    public AppStreamViewModel(DaemonClient client, int streamId, string label, IReadOnlyList<string> channels)
    {
        _client = client; StreamId = streamId; Label = label;
        foreach (string c in channels) Channels.Add(c);
    }

    public int StreamId { get; }
    public string Label { get; }
    public ObservableCollection<string> Channels { get; } = [];

    private string _channelId = "";
    public string ChannelId
    {
        get => _channelId;
        set { if (Set(ref _channelId, value) && !_applying && value.Length > 0) _ = _client.AssignStreamAsync(StreamId, value); }
    }

    public void ApplyFromDaemon(string channelId)
    {
        _applying = true;
        try { ChannelId = channelId; }
        finally { _applying = false; }
    }
}

/// <summary>One selectable sink or source. Own nodes are OpenXLR's own.</summary>
public sealed record AudioDeviceItem(string Name, string Description, bool IsOwn, bool IsPhysical = false)
{
    public string Label => IsOwn ? $"{Description} (OpenXLR)" : Description;
}

/// <summary>A mix (monitor/stream/chat): master level and mute.</summary>
public sealed class MixViewModel : ViewModelBase, IHasId
{
    private readonly DaemonClient _client;
    private bool _applying;

    public MixViewModel(DaemonClient client, string id, string name)
    {
        _client = client; Id = id; Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch($"mixvol:{Id}");
                SliderSync.Send($"mixvol:{Id}", () => _ = _client.SetMixVolumeAsync(Id, v));
            }
            Raise(nameof(VolumeText));
        }
    }
    public string VolumeText => $"{_volume * 100:0}%";

    private bool _muted;
    public bool Muted { get => _muted; set { if (Set(ref _muted, value) && !_applying) _ = _client.SetMixMutedAsync(Id, value); } }

    private double _meterL;
    public double MeterL { get => _meterL; set => Set(ref _meterL, Math.Min(value, 1.0)); }
    private double _meterR;
    public double MeterR { get => _meterR; set => Set(ref _meterR, Math.Min(value, 1.0)); }

    public void ApplyFromDaemon(JsonNode n)
    {
        _applying = true;
        try
        {
            if (!SliderSync.RecentlyTouched($"mixvol:{Id}"))
                Volume = n["volume"]?.GetValue<double>() ?? 1.0;
            Muted = n["muted"]?.GetValue<bool>() ?? false;
        }
        finally { _applying = false; }
    }
}

/// <summary>A channel with one send (level + mute) per mix.</summary>
public sealed class ChannelViewModel : ViewModelBase, IHasId
{
    public ChannelViewModel(DaemonClient client, string id, string name, IReadOnlyList<string> mixIds)
    {
        Id = id; Name = name;
        foreach (string mixId in mixIds) Sends.Add(new SendViewModel(client, id, mixId));
    }

    public string Id { get; }
    public string Name { get; }
    public ObservableCollection<SendViewModel> Sends { get; } = [];

    private double _meterL;
    public double MeterL { get => _meterL; set => Set(ref _meterL, Math.Min(value, 1.0)); }
    private double _meterR;
    public double MeterR { get => _meterR; set => Set(ref _meterR, Math.Min(value, 1.0)); }

    public void ApplyFromDaemon(JsonNode n)
    {
        var muted = new HashSet<string>();
        if (n["mutedIn"] is JsonArray arr)
            foreach (JsonNode? m in arr) if (m is not null) muted.Add(m.GetValue<string>());

        if (n["levels"] is JsonObject levels)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in levels)
            {
                SendViewModel? send = Sends.FirstOrDefault(s => s.MixId == kv.Key);
                if (send is null) continue;
                send.ApplyFromDaemon(kv.Value?.GetValue<double>() ?? 0.0, muted.Contains(kv.Key));
            }
        }
    }
}

/// <summary>One channel's send into one mix, the fader cell.</summary>
public sealed class SendViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly string _channelId;
    private bool _applying;

    public SendViewModel(DaemonClient client, string channelId, string mixId)
    {
        _client = client; _channelId = channelId; MixId = mixId;
    }

    public string MixId { get; }

    private double _level;
    public double Level
    {
        get => _level;
        set
        {
            if (Set(ref _level, value) && !_applying)
            {
                double v = value;
                string key = $"lvl:{_channelId}:{MixId}";
                SliderSync.Touch(key);
                SliderSync.Send(key, () => _ = _client.SetLevelAsync(_channelId, MixId, v));
            }
            Raise(nameof(LevelText));
        }
    }
    public string LevelText => $"{_level * 100:0}";

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set { if (Set(ref _muted, value) && !_applying) _ = _client.SetChannelMutedAsync(_channelId, MixId, value); }
    }

    public void ApplyFromDaemon(double level, bool muted)
    {
        _applying = true;
        try
        {
            if (!SliderSync.RecentlyTouched($"lvl:{_channelId}:{MixId}")) Level = level;
            Muted = muted;
        }
        finally { _applying = false; }
    }
}


/// <summary>
/// Keeps sliders responsive against the daemon's echo. Dragging a slider fires
/// its setter for every pixel; unthrottled, each send triggers a state push that
/// snaps the slider to a stale value mid-drag (rubber-banding). Send() batches
/// to a trailing edge every 80 ms, and RecentlyTouched() suppresses applying
/// echoes to a slider for 800 ms after the user moved it. UI-thread only.
/// </summary>
internal static class SliderSync
{
    private static readonly System.Collections.Generic.Dictionary<string, DateTime> Touched = [];
    private static readonly System.Collections.Generic.Dictionary<string, Action> Pending = [];
    private static DispatcherTimer? _timer;

    public static void Touch(string key) => Touched[key] = DateTime.UtcNow;

    public static bool RecentlyTouched(string key)
        => Touched.TryGetValue(key, out DateTime t) && DateTime.UtcNow - t < TimeSpan.FromMilliseconds(800);

    public static void Send(string key, Action send)
    {
        Pending[key] = send;
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled) _timer.Start();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            foreach (string key in System.Linq.Enumerable.ToList(Pending.Keys))
            {
                if (Pending.Remove(key, out Action? send)) send();
            }
            if (Pending.Count == 0) timer.Stop();
        };
        return timer;
    }
}
