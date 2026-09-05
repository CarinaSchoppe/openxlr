using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Shared restart state for the header and upgrade banner buttons.</summary>
public sealed class DaemonRestartViewModel : ViewModelBase
{
    private readonly Func<Task<bool>> _restart;
    private bool _busy;
    private string? _status;

    public DaemonRestartViewModel()
        : this(() => Task.Run(StartupIntegration.RestartDaemon)) { }

    internal DaemonRestartViewModel(Func<Task<bool>> restart) => _restart = restart;

    public bool CanRestart => !_busy;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>
    /// Run systemctl off the UI thread. Only one restart runs at a time, and a
    /// failed request leaves the buttons usable for another attempt.
    /// </summary>
    public async Task RestartAsync()
    {
        if (_busy) return;
        _busy = true;
        Raise(nameof(CanRestart));
        Status = "Restarting daemon...";
        try
        {
            Status = await _restart()
                ? "Service restarted. Waiting for the daemon connection."
                : "Restart failed. Check the user service logs; a manually started daemon must be restarted by hand.";
        }
        catch (Exception)
        {
            Status = "Restart failed. Check the user service logs.";
        }
        finally
        {
            _busy = false;
            Raise(nameof(CanRestart));
        }
    }
}
