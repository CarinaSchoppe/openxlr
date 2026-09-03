using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>One entry in the enforced-default pickers; Name null = don't enforce.</summary>
public sealed record DeviceChoice(string? Name, string Label);

/// <summary>
/// Backs the Options window. Startup toggles apply immediately to the system
/// (systemd unit, autostart entry) and persist in ui.json; the enforced-default
/// pickers go straight to the daemon, which owns that setting.
/// </summary>
public sealed class OptionsViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly MainViewModel _main;
    private bool _applying;

    /// <summary>Exposed for the diagnostics collector in the Options window.</summary>
    public DaemonClient Client => _client;

    public OptionsViewModel(DaemonClient client, MainViewModel main)
    {
        _client = client;
        _main = main;

        UiSettings s = UiSettings.Load();
        _startDaemonAtLogin = s.StartDaemonAtLogin;
        _openWindowAtLogin = s.OpenWindowAtLogin;
        _minimizeToTray = s.MinimizeToTray;
        _startMinimized = s.StartMinimized;
        // No saved choice means the daemon runs whatever its unit asked for,
        // which for every shipped unit is the submixer on.
        _submixer = DaemonPrefs.Load().Submixer ?? true;

        BuildChoices();
        _applying = true;
        try
        {
            EnforcedOutput = OutputChoices.FirstOrDefault(c => c.Name == main.EnforcedDefaultSink) ?? OutputChoices[0];
            EnforcedInput = InputChoices.FirstOrDefault(c => c.Name == main.EnforcedDefaultSource) ?? InputChoices[0];
        }
        finally { _applying = false; }
    }

    // --- startup behaviour ---

    private bool _startDaemonAtLogin;
    public bool StartDaemonAtLogin
    {
        get => _startDaemonAtLogin;
        set
        {
            if (DaemonStartupChanging || !Set(ref _startDaemonAtLogin, value)) return;
            _ = ChangeDaemonStartupAsync(value);
        }
    }

    private bool _daemonStartupChanging;
    public bool DaemonStartupChanging { get => _daemonStartupChanging; private set => Set(ref _daemonStartupChanging, value); }
    private string? _daemonStartupNote;
    public string? DaemonStartupNote { get => _daemonStartupNote; private set => Set(ref _daemonStartupNote, value); }

    private async System.Threading.Tasks.Task ChangeDaemonStartupAsync(bool enabled)
    {
        DaemonStartupChanging = true;
        DaemonStartupNote = "Updating service…";
        try
        {
            if (await StartupIntegration.SetDaemonAtLoginAsync(enabled))
            {
                Persist();
                DaemonStartupNote = null;
            }
            else
            {
                Set(ref _startDaemonAtLogin, !enabled, nameof(StartDaemonAtLogin));
                DaemonStartupNote = "Could not update autostart. Check the user systemd service and retry.";
            }
        }
        finally { DaemonStartupChanging = false; }
    }

    private bool _openWindowAtLogin;
    public bool OpenWindowAtLogin
    {
        get => _openWindowAtLogin;
        set
        {
            if (!Set(ref _openWindowAtLogin, value)) return;
            StartupIntegration.SetWindowAtLogin(value);
            Persist();
        }
    }

    private bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!Set(ref _minimizeToTray, value)) return;
            Persist();
            _main.MinimizeToTray = value;
        }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!Set(ref _startMinimized, value)) return;
            Persist();
        }
    }

    // --- submixer on/off (daemon-side setting, applied by restarting it) ---

    private bool _submixer;
    public bool Submixer
    {
        get => _submixer;
        set
        {
            if (SubmixerChanging || !Set(ref _submixer, value)) return;
            try
            {
                new DaemonPrefs { Submixer = value }.Save();
            }
            catch (Exception ex)
            {
                SubmixerNote = $"Could not save the setting: {ex.Message}";
                return;
            }
            _ = RestartSubmixerAsync();
        }
    }

    private bool _submixerChanging;
    public bool SubmixerChanging { get => _submixerChanging; private set => Set(ref _submixerChanging, value); }

    private async System.Threading.Tasks.Task RestartSubmixerAsync()
    {
        SubmixerChanging = true;
        SubmixerNote = "Requesting daemon restart…";
        try
        {
            SubmixerNote = await StartupIntegration.RestartDaemonAsync()
                ? "Restart requested. The mixer will reconnect automatically."
                : "Saved, but restart could not be requested. Run systemctl --user restart openxlr-daemon.";
        }
        finally { SubmixerChanging = false; }
    }

    private string? _submixerNote;
    public string? SubmixerNote
    {
        get => _submixerNote;
        private set => Set(ref _submixerNote, value);
    }

    // Start from the file so fields owned elsewhere (the main window's
    // collapsed tiles) survive a save from here.
    private void Persist() => (UiSettings.Load() with
    {
        StartDaemonAtLogin = _startDaemonAtLogin,
        OpenWindowAtLogin = _openWindowAtLogin,
        MinimizeToTray = _minimizeToTray,
        StartMinimized = _startMinimized,
    }).Save();

    // --- enforced system defaults ---

    public ObservableCollection<DeviceChoice> OutputChoices { get; } = [];
    public ObservableCollection<DeviceChoice> InputChoices { get; } = [];

    private DeviceChoice? _enforcedOutput;
    public DeviceChoice? EnforcedOutput
    {
        get => _enforcedOutput;
        set { if (Set(ref _enforcedOutput, value) && !_applying) SendEnforced(); }
    }

    private DeviceChoice? _enforcedInput;
    public DeviceChoice? EnforcedInput
    {
        get => _enforcedInput;
        set { if (Set(ref _enforcedInput, value) && !_applying) SendEnforced(); }
    }

    private void SendEnforced()
        => _ = _client.SetEnforcedDefaultsAsync(_enforcedOutput?.Name, _enforcedInput?.Name);

    private void BuildChoices()
    {
        OutputChoices.Add(new DeviceChoice(null, "(don't enforce)"));
        // "#phones" entries are channel-pair routing targets, not real sinks a
        // system default can point to.
        foreach (AudioDeviceItem d in _main.Outputs.Where(d => !d.Name.Contains("#phones", StringComparison.Ordinal)))
            OutputChoices.Add(new DeviceChoice(d.Name, d.Label));

        InputChoices.Add(new DeviceChoice(null, "(don't enforce)"));
        foreach (AudioDeviceItem d in _main.Inputs)
            InputChoices.Add(new DeviceChoice(d.Name, d.Label));
    }
}
