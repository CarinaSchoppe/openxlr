using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Shared restart state for the header, mismatch banner and Options.</summary>
public sealed class ServiceViewModel : ViewModelBase
{
    private readonly Func<Task<bool>> _restart;
    public ServiceViewModel() : this(StartupIntegration.RestartDaemonAsync) { }
    internal ServiceViewModel(Func<Task<bool>> restart) => _restart = restart;

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    public async Task RestartAsync()
    {
        if (Busy) return;
        Busy = true;
        Status = "Requesting daemon restart…";
        SessionLog.Write("service", "Manual daemon restart requested");
        try
        {
            Status = await _restart()
                ? "Restart requested. The mixer reconnects automatically."
                : "Restart unavailable. Check the systemd user service or collect diagnostics.";
        }
        catch (Exception ex)
        {
            Status = $"Restart failed: {ex.Message}";
        }
        finally
        {
            SessionLog.Write("service", Status ?? "Restart finished");
            Busy = false;
        }
    }
}
