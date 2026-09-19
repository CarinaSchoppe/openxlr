using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>The daemon owns the recording; this view only requests and displays it.</summary>
public sealed class SoundCheckViewModel(DaemonClient client, string channel) : ViewModelBase
{
    private bool _busy, _active;
    private string _mode = "idle";
    private double _seconds;
    private string? _error;
    public bool Active => _active;
    public bool CanRecord => !_busy && _mode != "recording";
    public bool CanLoop => !_busy && _active && _seconds >= 0.1;
    public bool CanStop => !_busy && _active;
    public string Status => _mode switch
    {
        "recording" => $"Recording: {_seconds:0.0} / 10 seconds",
        "looping" => $"Looping {_seconds:0.0} seconds through the live chain",
        "live" when _active => $"Live microphone. {_seconds:0.0} seconds ready to loop",
        _ => "Live microphone. No sample recorded",
    };
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public void Apply(JsonNode? state)
    {
        _active = state?["channel"]?.GetValue<string>() == channel;
        _mode = _active ? state?["mode"]?.GetValue<string>() ?? "idle" : "idle";
        _seconds = _active ? state?["seconds"]?.GetValue<double>() ?? 0 : 0;
        Error = state?["error"]?.GetValue<string>();
        Raise(nameof(Active)); Raise(nameof(Status)); RaiseButtons();
    }
    private void RaiseButtons() { Raise(nameof(CanRecord)); Raise(nameof(CanLoop)); Raise(nameof(CanStop)); }
    public async Task RunAsync(string action)
    {
        if (_busy) return;
        _busy = true; RaiseButtons();
        try { Error = await client.SoundCheckAsync(channel, action); }
        finally { _busy = false; RaiseButtons(); }
    }

    // Closing can follow a pending record command; send stop behind it rather
    // than dropping it because the buttons are temporarily disabled.
    public async Task StopOnCloseAsync() => Error = await client.SoundCheckAsync(channel, "stop");
}
