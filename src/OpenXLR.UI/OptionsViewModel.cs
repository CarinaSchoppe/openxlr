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
            if (!Set(ref _startDaemonAtLogin, value)) return;
            StartupIntegration.SetDaemonAtLogin(value);
            Persist();
        }
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

    private void Persist() => new UiSettings
    {
        StartDaemonAtLogin = _startDaemonAtLogin,
        OpenWindowAtLogin = _openWindowAtLogin,
        MinimizeToTray = _minimizeToTray,
        StartMinimized = _startMinimized,
    }.Save();

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
