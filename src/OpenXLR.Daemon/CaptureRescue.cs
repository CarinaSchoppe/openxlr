using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// Brings back the microphone of a unit whose capture stalls under its own
/// playback (<see cref="DeviceCapabilities.CaptureRecovery"/>, the Wave:3
/// MK.2): every mix comes off the unit's sink, the unit is reset on USB, and
/// the mixes go back once the re-enumerated card's capture runs. The order is
/// the one that cleared the stall on the maintainer's unit every time. The
/// watch that decides when (<see cref="CaptureWatch"/>) runs once per mixer
/// sweep; a client asks for the same with the recoverCapture command.
/// </summary>
internal sealed class CaptureRescue(
    ILogger log,
    ICaptureRecoveryMixer mixer,
    Func<DeviceInfo?> unit,
    Func<DeviceCapabilities?> capabilities,
    Func<string, string?> resetUnit,
    CaptureWatch? watch = null,
    Func<string, float[]?>? sample = null)
{
    private readonly CaptureWatch _watch = watch ?? new CaptureWatch();
    private readonly Func<string, float[]?> _sample = sample ?? CaptureWatch.Sample;
    private readonly object _gate = new();

    /// <summary>One look at the driven unit's capture; recovers it when the watch says so.</summary>
    public void Sweep()
    {
        bool applies = capabilities()?.CaptureRecovery == true && !mixer.CaptureRecoveryActive;
        CaptureVerdict verdict = _watch.Observe(applies ? mixer.DrivenUnitPlayback() : null, mixer.ProbeCapture, _sample);
        if (verdict.Note is { } note) log.LogWarning("{note}", note);
        if (verdict.Recover) Recover(verdict.Reason!);
    }

    /// <summary>
    /// Recover the driven unit's capture now. <paramref name="reason"/> says
    /// why, for the journal. Null once the unit is reset and its mixes wait
    /// for its capture, else why nothing was done.
    /// </summary>
    public string? Recover(string reason)
    {
        lock (_gate)
        {
            DeviceInfo? driven = unit();
            if (driven is null || capabilities()?.CaptureRecovery != true)
                return "recoverCapture: the driven interface does not recover its capture by a USB reset";
            if (mixer.CaptureRecoveryActive) return "recoverCapture: a recovery is already under way";
            // Playback first off the unit, so its capture is the stream
            // running when the reset brings the card back.
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
}
