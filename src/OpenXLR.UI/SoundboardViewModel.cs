using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>What the daemon answered an import with: whether the clip was added, its sentence and the new clip's id.</summary>
public sealed record ClipImportResult(bool Ok, string Message, string? Clip);

/// <summary>The soundboard's commands. The daemon client sends them; a test records them.</summary>
public interface ISoundboardCommands
{
    /// <summary><c>setSoundboard</c>; resolves to null, or the daemon's refusal.</summary>
    Task<string?> SetSoundboardAsync(bool enabled);

    /// <summary><c>importClip</c> with a path the user picked; null when the daemon did not answer.</summary>
    Task<ClipImportResult?> ImportClipAsync(string path);

    /// <summary><c>updateClip</c> with the fields that change; resolves to null, or the daemon's refusal.</summary>
    Task<string?> UpdateClipAsync(string clip, IReadOnlyDictionary<string, object> fields);

    Task<string?> DeleteClipAsync(string clip);

    Task<string?> SetClipOrderAsync(IReadOnlyList<string> clips);

    /// <summary><c>playClip</c> in the clip's own mode.</summary>
    Task<string?> PlayClipAsync(string clip);

    Task<string?> ReleaseClipAsync(string clip);

    Task<string?> StopClipAsync(string clip);

    Task<string?> StopClipsAsync();
}

/// <summary>
/// One clip of the soundboard as a pad draws it: the library entry, whether
/// the player holds it, whether it plays and how far along it is. Every value
/// is the daemon's; the position comes from the <c>clip:&lt;id&gt;</c> meter.
/// </summary>
public sealed class SoundClipViewModel : ViewModelBase, IHasId
{
    /// <summary>The press modes in the order the edit window lists them.</summary>
    internal static readonly string[] Modes = ["overlap", "restart", "toggle", "hold"];

    /// <summary>A clip with no icon of its own wears SFX's default, as its Deck key does.</summary>
    internal static readonly string DefaultIcon = LayoutDefaultIcons.ForChannel(SoundboardViewModel.DefaultChannel, false, null);

    internal SoundClipViewModel(SoundboardViewModel board, string id)
    {
        Board = board;
        Id = id;
    }

    public SoundboardViewModel Board { get; }

    public string Id { get; }

    private string _name = "";
    public string Name { get => _name; private set { if (Set(ref _name, value)) RaiseText(); } }

    private double _seconds;
    public double Seconds { get => _seconds; private set { if (Set(ref _seconds, value)) RaiseText(); } }

    private double _gain = 1;
    /// <summary>The clip's own gain, 0 to 1, before SFX's fader.</summary>
    public double Gain { get => _gain; private set { if (Set(ref _gain, value)) RaiseText(); } }

    private string _mode = "overlap";
    /// <summary>What a press does: <c>overlap</c>, <c>restart</c>, <c>toggle</c> or <c>hold</c>.</summary>
    public string Mode { get => _mode; private set { if (Set(ref _mode, value)) { Raise(nameof(IsHold)); RaiseText(); } } }

    private bool _loop;
    public bool Loop { get => _loop; private set { if (Set(ref _loop, value)) RaiseText(); } }

    /// <summary>The clip's icon and colour, drawn like a channel's.</summary>
    public LayoutAppearanceViewModel Appearance { get; } = new();

    private string _status = "off";
    /// <summary>
    /// The daemon's one word for the clip: <c>ready</c> when a press plays it,
    /// <c>loading</c>, <c>error</c> with the reason in <see cref="Error"/>, or
    /// <c>off</c> while the board cannot play, the reason in the board's error.
    /// </summary>
    public string Status { get => _status; private set { if (Set(ref _status, value)) { Raise(nameof(Loaded)); RaiseState(); } } }

    /// <summary>The player holds the clip, so a press plays it at once.</summary>
    public bool Loaded => _status == "ready";

    private string? _error;
    /// <summary>Why the player does not hold the clip, in the daemon's words, or null.</summary>
    public string? Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); RaiseState(); } } }

    public bool HasError => _status == "error" && !string.IsNullOrEmpty(_error);

    private bool _statePlaying, _meterPlaying;
    /// <summary>A voice plays the clip: the state says so, or the last meter frame did.</summary>
    public bool Playing => _statePlaying || _meterPlaying;

    private double _position;
    /// <summary>How far the newest voice is, 0 to 1; 0 while the clip is silent.</summary>
    public double Position { get => _position; private set => Set(ref _position, value); }

    private int _voices;
    /// <summary>How many voices play the clip now.</summary>
    public int Voices { get => _voices; private set => Set(ref _voices, value); }

    /// <summary>The key has to come up for the clip to end: the pad sends a release.</summary>
    public bool IsHold => _mode == "hold";

    /// <summary>A press plays the clip: the daemon says it is ready.</summary>
    public bool CanPlay => _status == "ready";

    /// <summary>The player is loading the clip.</summary>
    public bool Loading => _status == "loading";

    /// <summary>The mode's name, as the pad and the edit window say it.</summary>
    public string ModeName => ModeNameOf(_mode);

    internal static string ModeNameOf(string mode) => mode switch
    {
        "restart" => Localizer.Text("SoundClipModeRestart"),
        "toggle" => Localizer.Text("SoundClipModeToggle"),
        "hold" => Localizer.Text("SoundClipModeHold"),
        _ => Localizer.Text("SoundClipModeOverlap"),
    };

    /// <summary>The pad's second line: why it cannot play, that it is loading, or its length and mode.</summary>
    public string Detail => HasError ? _error!
        : Loading ? Localizer.Text("SoundClipLoading")
        : _loop ? Localizer.Format("SoundClipDetailLoop", _seconds, ModeName)
        : Localizer.Format("SoundClipDetail", _seconds, ModeName);

    /// <summary>The pad's name for a screen reader: the clip, what a press does, and whether it plays.</summary>
    public string PadName => Playing ? Localizer.Format("SoundPadNamePlaying", _name, ModeName)
        : Localizer.Format("SoundPadName", _name, ModeName);

    /// <summary>The pad's tooltip: its name, its length, mode and gain, or why it cannot play.</summary>
    public string Tip => $"{_name}\n{Detail}\n{Localizer.Format("SoundClipGainTip", Math.Round(_gain * 100))}";

    /// <summary>The menu button's name for a screen reader.</summary>
    public string MenuName => Localizer.Format("SoundClipMenuName", _name);

    private void RaiseText()
    {
        Raise(nameof(ModeName));
        Raise(nameof(Detail));
        Raise(nameof(PadName));
        Raise(nameof(Tip));
        Raise(nameof(MenuName));
    }

    internal void RaiseState()
    {
        Raise(nameof(CanPlay));
        Raise(nameof(Loading));
        RaiseText();
    }

    /// <summary>Take the clip's entry from a state push.</summary>
    internal void Apply(JsonObject clip)
    {
        Name = Text(clip["name"]) ?? Id;
        Seconds = Number(clip["seconds"]) ?? 0;
        Gain = Math.Clamp(Number(clip["gain"]) ?? 1, 0, 1);
        Mode = Text(clip["mode"]) is { } mode && Modes.Contains(mode) ? mode : "overlap";
        Loop = Flag(clip["loop"]);
        Appearance.Apply(new JsonObject { ["icon"] = Text(clip["icon"]) ?? "", ["colour"] = Text(clip["colour"]), ["defaultIcon"] = DefaultIcon });
        Error = Text(clip["error"]) is { Length: > 0 } error ? error : null;
        // A daemon from before the word worked it out from loaded and error.
        string? status = Text(clip["status"]);
        Status = status is "ready" or "loading" or "error" or "off" ? status
            : Flag(clip["loaded"]) ? "ready" : _error is not null ? "error" : "loading";
        bool playing = Flag(clip["playing"]);
        if (_statePlaying != playing)
        {
            _statePlaying = playing;
            RaisePlaying();
        }
    }

    /// <summary>Take the clip's <c>[position, voices]</c> from a meter frame, or null when the frame has none.</summary>
    internal void ApplyMeter(JsonArray? meter)
    {
        double position = meter is { Count: > 0 } && Number(meter[0]) is double at ? Math.Clamp(at, 0, 1) : 0;
        int voices = meter is { Count: > 1 } && Number(meter[1]) is double count ? (int)Math.Max(0, count) : meter is null ? 0 : 1;
        Position = position;
        Voices = voices;
        if (_meterPlaying != voices > 0)
        {
            _meterPlaying = voices > 0;
            RaisePlaying();
        }
    }

    private void RaisePlaying()
    {
        Raise(nameof(Playing));
        Raise(nameof(PadName));
        if (!Playing) Position = 0;
    }

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    internal static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) && flag;

    internal static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number)
        && double.IsFinite(number) ? number : null;
}

/// <summary>
/// The soundboard as the Soundboard window shows it: whether the daemon has
/// one, whether this installation can play it, whether it is on, the channel
/// its clips play into, the clips in library order, and a line saying what
/// the last import or change did. The board's master is SFX's own channel
/// volume, taken from the mixer's channel and never held here. Pressing a
/// pad sends <c>playClip</c>, and a hold pad's release sends
/// <c>releaseClip</c>; nothing is played here.
/// </summary>
public sealed class SoundboardViewModel : ViewModelBase
{
    /// <summary>The channel the clips play into while the daemon names none, as the daemon names it.</summary>
    internal const string DefaultChannel = "sfx";

    private readonly ISoundboardCommands _commands;
    private bool _applying;

    public SoundboardViewModel(ISoundboardCommands commands) => _commands = commands;

    private bool _reported;
    /// <summary>The daemon reports a soundboard: the buttons that open the window show.</summary>
    public bool IsReported { get => _reported; private set { if (Set(ref _reported, value)) RaiseBoard(); } }

    private bool _available;
    /// <summary>This installation has the native helper the player needs.</summary>
    public bool Available { get => _available; private set { if (Set(ref _available, value)) RaiseBoard(); } }

    private bool _enabled;
    /// <summary>The board's switch: on starts the player into SFX, creating SFX when the layout has none.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Raise();
            RaiseBoard();
            if (!_applying) Send(_commands.SetSoundboardAsync(value));
        }
    }

    private string? _channelId;
    /// <summary>The channel the clips play into, <c>sfx</c>, or null after that channel was deleted.</summary>
    public string? ChannelId { get => _channelId; private set { if (Set(ref _channelId, value)) RaiseBoard(); } }

    private ChannelViewModel? _channel;
    /// <summary>
    /// The mixer's own SFX channel, whose volume is the board's master: the
    /// window's fader is bound to the very object the strip draws.
    /// </summary>
    public ChannelViewModel? Channel
    {
        get => _channel;
        private set
        {
            if (ReferenceEquals(_channel, value)) return;
            if (_channel is not null) _channel.PropertyChanged -= OnChannelChanged;
            _channel = value;
            if (_channel is not null) _channel.PropertyChanged += OnChannelChanged;
            Raise();
            Raise(nameof(HasMaster));
            Raise(nameof(ShowsChannel));
            Raise(nameof(ChannelName));
            Raise(nameof(MasterHint));
            Raise(nameof(MasterName));
        }
    }

    private void OnChannelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelViewModel.HasVolume)) Raise(nameof(HasMaster));
        if (e.PropertyName != nameof(ChannelViewModel.Name)) return;
        Raise(nameof(ChannelName));
        Raise(nameof(MasterHint));
        Raise(nameof(MasterName));
        Raise(nameof(Notice));
    }

    /// <summary>The header names the channel the clips play into: the mixer has it.</summary>
    public bool ShowsChannel => _channel is not null;

    /// <summary>The window draws SFX's volume as the board's master: the channel is there and has a volume.</summary>
    public bool HasMaster => _channel is { HasVolume: true };

    /// <summary>The channel's own name, which the user may have changed.</summary>
    public string ChannelName => _channel?.Name ?? "SFX";

    /// <summary>The line beside the master: whose fader it is and what decides where the clips are heard.</summary>
    public string MasterHint => Localizer.Format("SoundboardMasterHint", ChannelName);

    /// <summary>The master fader's name for a screen reader.</summary>
    public string MasterName => Localizer.Format("ChannelVolumeName", ChannelName);

    private string? _error;
    /// <summary>Why the board cannot play, in the daemon's own sentence; null exactly when it can.</summary>
    public string? Error { get => _error; private set { if (Set(ref _error, value)) RaiseBoard(); } }

    private IReadOnlyList<string> _formats = [];
    /// <summary>What this installation decodes, by extension: the file picker offers these.</summary>
    public IReadOnlyList<string> Formats => _formats;

    public ObservableCollection<SoundClipViewModel> Clips { get; } = [];

    public bool HasClips => Clips.Count > 0;

    /// <summary>The window shows the line that says there are no clips yet.</summary>
    public bool ShowsEmpty => _reported && Clips.Count == 0;

    /// <summary>Any clip plays: Stop all lights.</summary>
    public bool AnyPlaying => Clips.Any(c => c.Playing);

    /// <summary>The board can play: the daemon reports it and names no reason it cannot.</summary>
    public bool CanPlay => _reported && _error is null;

    /// <summary>Clips can be added: the daemon has the board and this installation the helper that checks them.</summary>
    public bool CanImport => _reported && _available;

    /// <summary>The window offers to turn the board on: it is off and could run.</summary>
    public bool ShowsTurnOn => _reported && _available && (!_enabled || _channelId is null);

    /// <summary>The turn-on button says it creates SFX when there is none.</summary>
    public string TurnOnText => _channelId is null ? Localizer.Text("SoundboardTurnOnCreate") : Localizer.Text("SoundboardTurnOn");

    /// <summary>
    /// The sentence over the pads while they cannot play: the daemon's own,
    /// or that the daemon has no soundboard. Empty while they can.
    /// </summary>
    public string Notice => !_reported ? Localizer.Text("SoundboardNotReported") : _error ?? "";

    public bool HasNotice => Notice.Length > 0;

    /// <summary>The notice is a failure rather than a board the user turned off.</summary>
    public bool NoticeWarns => !_reported || _error is not null && !(_available && !_enabled && _channelId is not null);

    private string _status = "";
    /// <summary>What the last import, change or press came to, or empty.</summary>
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }

    public bool HasStatus => _status.Length > 0;

    private bool _statusWarns;
    /// <summary>The status line is a refusal.</summary>
    public bool StatusWarns { get => _statusWarns; private set => Set(ref _statusWarns, value); }

    private bool _importing;
    /// <summary>An import is running: the add buttons wait for it.</summary>
    public bool Importing { get => _importing; private set { if (Set(ref _importing, value)) Raise(nameof(CanAdd)); } }

    public bool CanAdd => CanImport && !_importing;

    private void RaiseBoard()
    {
        Raise(nameof(CanPlay));
        Raise(nameof(CanImport));
        Raise(nameof(CanAdd));
        Raise(nameof(ShowsTurnOn));
        Raise(nameof(TurnOnText));
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
        Raise(nameof(NoticeWarns));
        Raise(nameof(ShowsEmpty));
        foreach (SoundClipViewModel clip in Clips) clip.RaiseState();
    }

    /// <summary>
    /// Take <c>mixer.soundboard</c> from a state push, with the mixer's
    /// channels for the master. A daemon without a soundboard, or no mixer,
    /// passes null.
    /// </summary>
    public void Apply(JsonNode? board, IEnumerable<ChannelViewModel> channels)
    {
        _applying = true;
        try
        {
            if (board is not JsonObject state)
            {
                IsReported = false;
                Available = false;
                Enabled = false;
                ChannelId = null;
                Error = null;
                _formats = [];
                Channel = null;
                Clips.Clear();
                RaiseClips();
                return;
            }
            IsReported = true;
            Available = SoundClipViewModel.Flag(state["available"]);
            Enabled = SoundClipViewModel.Flag(state["enabled"]);
            ChannelId = SoundClipViewModel.Text(state["channel"]);
            Error = SoundClipViewModel.Text(state["error"]) is { Length: > 0 } error ? error : null;
            _formats = [.. (state["formats"] as JsonArray ?? []).Select(SoundClipViewModel.Text).OfType<string>()
                .Where(f => f.Length > 0 && f.All(char.IsAsciiLetterOrDigit))];
            Channel = _channelId is null ? null : channels.FirstOrDefault(c => c.Id == _channelId);
            SyncClips(state["clips"] as JsonArray ?? []);
            RaiseBoard();
        }
        finally { _applying = false; }
    }

    private void SyncClips(JsonArray clips)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;
        foreach (JsonObject clip in clips.OfType<JsonObject>())
        {
            if (SoundClipViewModel.Text(clip["id"]) is not { Length: > 0 } id || !seen.Add(id)) continue;
            SoundClipViewModel? existing = Clips.FirstOrDefault(c => c.Id == id);
            if (existing is null)
            {
                existing = new SoundClipViewModel(this, id);
                existing.Apply(clip);
                Clips.Insert(position, existing);
            }
            else
            {
                existing.Apply(clip);
                int at = Clips.IndexOf(existing);
                if (at != position) Clips.Move(at, position);
            }
            position++;
        }
        for (int i = Clips.Count - 1; i >= 0; i--)
            if (!seen.Contains(Clips[i].Id)) Clips.RemoveAt(i);
        RaiseClips();
    }

    private void RaiseClips()
    {
        Raise(nameof(HasClips));
        Raise(nameof(ShowsEmpty));
        Raise(nameof(AnyPlaying));
    }

    /// <summary>Route each <c>clip:&lt;id&gt;</c> meter to its pad; a pad absent from the frame is silent.</summary>
    public void ApplyMeters(JsonObject levels)
    {
        if (Clips.Count == 0) return;
        bool before = AnyPlaying;
        foreach (SoundClipViewModel clip in Clips)
            clip.ApplyMeter(levels[$"clip:{clip.Id}"] as JsonArray);
        if (before != AnyPlaying) Raise(nameof(AnyPlaying));
    }

    /// <summary>The state no longer holds: the daemon went away.</summary>
    public void Reset() => Apply(null, []);

    // --- presses ---

    /// <summary>A pad went down: play the clip in its own mode.</summary>
    public void Press(SoundClipViewModel clip)
    {
        if (!clip.CanPlay) return;
        Send(_commands.PlayClipAsync(clip.Id));
    }

    /// <summary>A pad came up: a hold clip ends.</summary>
    public void Release(SoundClipViewModel clip)
    {
        if (clip.IsHold && _reported) Send(_commands.ReleaseClipAsync(clip.Id));
    }

    public void Stop(SoundClipViewModel clip)
    {
        if (_reported) Send(_commands.StopClipAsync(clip.Id));
    }

    public void StopAll()
    {
        if (_reported) Send(_commands.StopClipsAsync());
    }

    /// <summary>The window's turn-on button, which creates SFX when the layout has none.</summary>
    public void TurnOn()
    {
        if (!ShowsTurnOn) return;
        _enabled = true;
        Raise(nameof(Enabled));
        RaiseBoard();
        Send(_commands.SetSoundboardAsync(true));
    }

    // --- the library ---

    /// <summary>
    /// Move a clip one place earlier (-1) or later (+1). The pads move at
    /// once; the daemon's next state confirms or restores the order.
    /// </summary>
    public void Move(SoundClipViewModel clip, int delta)
    {
        int at = Clips.IndexOf(clip), to = at + delta;
        if (at < 0 || to < 0 || to >= Clips.Count) return;
        Clips.Move(at, to);
        Send(_commands.SetClipOrderAsync([.. Clips.Select(c => c.Id)]));
    }

    public bool CanMove(SoundClipViewModel clip, int delta)
    {
        int to = Clips.IndexOf(clip) + delta;
        return Clips.Contains(clip) && to >= 0 && to < Clips.Count;
    }

    /// <summary>Change a clip's entry; resolves to null, or the daemon's refusal, which the caller shows.</summary>
    public Task<string?> UpdateAsync(SoundClipViewModel clip, IReadOnlyDictionary<string, object> fields)
        => fields.Count == 0 ? Task.FromResult<string?>(null) : _commands.UpdateClipAsync(clip.Id, fields);

    /// <summary>Delete a clip and its copy; the status line says what happened.</summary>
    public async Task DeleteAsync(SoundClipViewModel clip)
    {
        string? error = await _commands.DeleteClipAsync(clip.Id);
        Show(error ?? Localizer.Format("SoundClipDeleted", clip.Name), error is not null);
    }

    /// <summary>
    /// Import each path in turn, one at a time, and say how it went: the
    /// daemon's own sentence for a single file, a count and the refusals for
    /// several.
    /// </summary>
    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !CanAdd) return;
        Importing = true;
        try
        {
            int added = 0;
            var refusals = new List<string>();
            string? only = null;
            for (int i = 0; i < paths.Count; i++)
            {
                if (paths.Count > 1) Show(Localizer.Format("SoundboardImporting", i + 1, paths.Count), false);
                ClipImportResult? result = await _commands.ImportClipAsync(paths[i]);
                string name = Path.GetFileName(paths[i]);
                if (result is null) refusals.Add(Localizer.Format("NoAnswerForPath", name));
                else if (result.Ok) { added++; only = result.Message; }
                else refusals.Add(paths.Count > 1 ? $"{name}: {result.Message}" : result.Message);
            }
            if (paths.Count == 1) Show(refusals.Count > 0 ? refusals[0] : only ?? "", refusals.Count > 0);
            else
            {
                string count = Localizer.Format("SoundboardImported", added, paths.Count);
                Show(refusals.Count == 0 ? count : count + "\n" + string.Join("\n", refusals), refusals.Count > 0);
            }
        }
        finally { Importing = false; }
    }

    /// <summary>
    /// The files in a folder this installation can import, by extension, in
    /// name order: the folder's own files, not its subfolders. Null, with the
    /// reason under the pads, when the folder cannot be read.
    /// </summary>
    public IReadOnlyList<string>? ClipFilesIn(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder)
                .Where(f => _formats.Contains(Path.GetExtension(f).TrimStart('.').ToLowerInvariant(), StringComparer.Ordinal))
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Show(ex.Message, true);
            return null;
        }
    }

    /// <summary>Import a folder's clips, or say it has none.</summary>
    public async Task ImportFolderAsync(string folder)
    {
        if (ClipFilesIn(folder) is not { } files) return;
        if (files.Count == 0)
        {
            Show(Localizer.Format("SoundboardFolderEmpty", Path.GetFileName(folder.TrimEnd('/'))), true);
            return;
        }
        await ImportAsync(files);
    }

    /// <summary>Show a line under the pads.</summary>
    internal void Show(string text, bool warns)
    {
        StatusWarns = warns;
        Status = text;
    }

    /// <summary>Send a command, and show the daemon's refusal when it has one.</summary>
    private async void Send(Task<string?> command)
    {
        string? error;
        try { error = await command; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { error = ex.Message; }
        if (error is not null) Show(error, true);
        else if (_statusWarns) Show("", false);
    }
}

/// <summary>
/// The edit window's copy of one clip: its name, gain, press mode, loop,
/// icon and colour as the user changes them. Saving sends only the fields
/// that changed, in one <c>updateClip</c>, and shows the daemon's refusal.
/// </summary>
public sealed class SoundClipEditViewModel : ViewModelBase
{
    private readonly SoundClipViewModel _clip;

    public SoundClipEditViewModel(SoundClipViewModel clip)
    {
        _clip = clip;
        _name = clip.Name;
        _gainPercent = Math.Round(clip.Gain * 100);
        _modeIndex = Math.Max(0, Array.IndexOf(SoundClipViewModel.Modes, clip.Mode));
        _loop = clip.Loop;
        _colour = clip.Appearance.Colour ?? "";
        Icon = clip.Appearance.Icon;
    }

    public SoundClipViewModel Clip => _clip;

    /// <summary>The window's title.</summary>
    public string Title => Localizer.Format("SoundClipEditTitle", _clip.Name);

    /// <summary>The press modes by name, in the order <see cref="ModeIndex"/> counts.</summary>
    public IReadOnlyList<string> ModeNames { get; } = [.. SoundClipViewModel.Modes.Select(SoundClipViewModel.ModeNameOf)];

    private string _name;
    public string Name { get => _name; set => Set(ref _name, value); }

    private double _gainPercent;
    /// <summary>The clip's gain in percent, 0 to 100.</summary>
    public double GainPercent { get => _gainPercent; set { if (Set(ref _gainPercent, value)) Raise(nameof(GainText)); } }

    public string GainText => $"{_gainPercent:0}%";

    private int _modeIndex;
    public int ModeIndex { get => _modeIndex; set { if (Set(ref _modeIndex, value)) Raise(nameof(ModeHint)); } }

    /// <summary>What a press does in the chosen mode.</summary>
    public string ModeHint => SoundClipViewModel.Modes.ElementAtOrDefault(_modeIndex) switch
    {
        "restart" => Localizer.Text("SoundClipModeRestartHint"),
        "toggle" => Localizer.Text("SoundClipModeToggleHint"),
        "hold" => Localizer.Text("SoundClipModeHoldHint"),
        _ => Localizer.Text("SoundClipModeOverlapHint"),
    };

    private bool _loop;
    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    private string _colour;
    /// <summary>#RRGGBB, or empty for none.</summary>
    public string Colour { get => _colour; set => Set(ref _colour, value); }

    /// <summary>The chosen icon's stored value; the window's picker sets it.</summary>
    public string Icon { get; set; }

    private string _note = "";
    /// <summary>Why the last save did not happen, or empty.</summary>
    public string Note { get => _note; private set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }

    public bool HasNote => _note.Length > 0;

    /// <summary>The fields that differ from the clip, as <c>updateClip</c> takes them; null with a note when one is not valid.</summary>
    internal Dictionary<string, object>? Changes()
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        string name = (_name ?? "").Trim();
        if (name.Length is 0 or > 60 || name.Any(char.IsControl))
        {
            Note = Localizer.Text("SoundClipNameHint");
            return null;
        }
        string colour = (_colour ?? "").Trim();
        if (colour.Length > 0 && !IsHexColour(colour))
        {
            Note = Localizer.Text("ColourFormatHint");
            return null;
        }
        if (name != _clip.Name) fields["name"] = name;
        double gain = Math.Clamp(Math.Round(_gainPercent) / 100, 0, 1);
        if (Math.Abs(gain - _clip.Gain) > 0.004) fields["gain"] = gain;
        string mode = SoundClipViewModel.Modes.ElementAtOrDefault(_modeIndex) ?? _clip.Mode;
        if (mode != _clip.Mode) fields["mode"] = mode;
        if (_loop != _clip.Loop) fields["loop"] = _loop;
        if ((Icon ?? "") != _clip.Appearance.Icon) fields["icon"] = Icon ?? "";
        if (!string.Equals(colour, _clip.Appearance.Colour ?? "", StringComparison.OrdinalIgnoreCase)) fields["colour"] = colour;
        return fields;
    }

    /// <summary>Send the changes; true once the daemon took them, or when nothing changed.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Changes() is not { } fields) return false;
        string? error = await _clip.Board.UpdateAsync(_clip, fields);
        Note = error ?? "";
        return error is null;
    }

    private static bool IsHexColour(string text) => text.Length == 7 && text[0] == '#' && text.Skip(1).All(char.IsAsciiHexDigit);
}
