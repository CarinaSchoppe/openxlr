using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// Brings back the microphone of a unit whose capture stalls under its own
/// playback (<see cref="DeviceCapabilities.CaptureRecovery"/>, the Wave:3
/// MK.2). The watch (<see cref="CaptureWatch"/>) runs once per mixer sweep
/// and, when the kernel keeps the capture stopped, takes every mix off the
/// unit's sink, resets the unit on USB and lets the mixes go back once the
/// re-enumerated card's capture runs; that order cleared the stall on the
/// maintainer's unit every time. The recoverCapture command tries the cheap
/// step first: the unit's capture links are dropped and made again, and only
/// a capture the kernel still does not run is reset the same way.
/// </summary>
internal sealed class CaptureRescue(
    ILogger log,
    ICaptureRecoveryMixer mixer,
    Func<DeviceInfo?> unit,
    Func<DeviceCapabilities?> capabilities,
    Func<string, string?> resetUnit,
    CaptureWatch? watch = null,
    Action<TimeSpan>? wait = null)
{
    /// <summary>How long a relinked capture is given before the kernel is asked whether it runs.</summary>
    internal static readonly TimeSpan RelinkSettle = TimeSpan.FromSeconds(2);

    private readonly CaptureWatch _watch = watch ?? new CaptureWatch();
    private readonly Action<TimeSpan> _wait = wait ?? Thread.Sleep;
    private readonly object _gate = new();

    /// <summary>One look at the driven unit's capture; resets it when the watch says so.</summary>
    public void Sweep()
    {
        bool applies = capabilities()?.CaptureRecovery == true && !mixer.CaptureRecoveryActive;
        CaptureVerdict verdict = _watch.Observe(applies ? mixer.DrivenUnitPlayback() : null, mixer.ProbeCapture);
        if (verdict.Note is { } note) log.LogWarning("{note}", note);
        if (verdict.Recover)
            lock (_gate) Reset(unit(), verdict.Reason!);
    }

    /// <summary>
    /// The recoverCapture command: drop and remake the driven unit's capture
    /// links, then reset the unit on USB only when the kernel still does not
    /// run its capture. Null once the capture runs again after the relink, or
    /// once the unit is reset and its mixes wait for its capture; else why
    /// nothing was done.
    /// </summary>
    public string? Recover()
    {
        lock (_gate)
        {
            DeviceInfo? driven = unit();
            if (driven is null || capabilities()?.CaptureRecovery != true)
                return "recoverCapture: the driven interface does not recover its capture";
            if (mixer.CaptureRecoveryActive) return "recoverCapture: a recovery is already under way";
            if (mixer.RelinkDrivenUnitCapture() is not { } relink) return "recoverCapture: the mixer is not built";
            log.LogWarning("{dev}: a client asked for its microphone to be recovered. Dropped and made again {n} link(s) out of {source}",
                driven.DisplayName, relink.Links, relink.Source);
            _wait(RelinkSettle);
            CaptureRun run = mixer.ProbeCapture(relink.Source);
            if (run.Started)
            {
                _watch.Recovered();
                log.LogInformation("{dev}: its capture runs after the relink; no USB reset", driven.DisplayName);
                return null;
            }
            string state = run.NodeId is null ? "is missing from the graph"
                : run.KernelRunning == false ? "is still stopped in the kernel" : "does not run";
            return Reset(driven, $"its capture {state} after the relink");
        }
    }

    /// <summary>Mixes off the unit's sink, then the USB reset. Called under the gate.</summary>
    private string? Reset(DeviceInfo? driven, string reason)
    {
        if (driven is null || capabilities()?.CaptureRecovery != true)
            return "recoverCapture: the driven interface does not recover its capture by a USB reset";
        if (mixer.CaptureRecoveryActive) return "recoverCapture: a recovery is already under way";
        // Playback first off the unit, so its capture is the stream running
        // when the reset brings the card back.
        if (mixer.BeginCaptureRecovery() is not int taken) return "recoverCapture: the mixer is not built";
        log.LogWarning("{dev}: {reason}. Recovering its microphone: took {n} mix route(s) off its own output, resetting it on USB",
            driven.DisplayName, reason, taken);
        string? failed = resetUnit(driven.InstanceId);
        _watch.Recovered();
        if (failed is not null)
        {
            mixer.EndCaptureRecovery();
            log.LogWarning("{dev}: the microphone recovery stopped ({error}); its mixes go back on its output", driven.DisplayName, failed);
            return "recoverCapture: " + failed;
        }
        log.LogInformation("{dev}: reset on USB; its mixes go back on its output once its capture runs again", driven.DisplayName);
        return null;
    }
}
