namespace OpenXLR.Core.Mixing;

/// <summary>
/// The gain of each capture input on a source OpenXLR does not drive: the
/// source's PipeWire volume, read as the device's own gain where it has a
/// hardware capture control (see <see cref="CaptureGain"/>). The sweep reads
/// it again whenever the registry shows the source's volume moved, so a
/// change made by another program (a voice chat's automatic gain, a
/// browser, a desktop volume applet) is reported and never written back.
/// Only <see cref="SetCaptureGain"/> writes it.
/// </summary>
public sealed partial class Mixer
{
    private readonly Dictionary<string, CaptureGainStatus> _captureGains = new(StringComparer.Ordinal);
    private static readonly TimeSpan HardwareMinimumRetry = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, (double? Value, long? FailedAt)> _hardwareMinimums = new(StringComparer.Ordinal);
    private string? _captureGainKey;
    private long? _captureGainRetrySince;

    /// <summary>
    /// The source a capture input's gain is read from and written to, or
    /// null when the channel has no gain of its own: it carries a Wave unit's
    /// input, whose gain is the unit's (<see cref="ChannelStatus.UnitInput"/>),
    /// it remembers a supported unit, or its source is one OpenXLR never
    /// writes (<see cref="CaptureGain.IsProtectedSource"/>).
    /// </summary>
    private static string? CaptureGainSource(ChannelDefinition channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
        => channel.CaptureSource is { } source && channel.CaptureUnit is null && !carried.ContainsKey(channel.Id)
            && !CaptureGain.IsProtectedSource(source) ? source : null;

    /// <summary>The gain the state reports for a channel, or null.</summary>
    private CaptureGainStatus? CaptureGainLocked(ChannelDefinition channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
        => CaptureGainSource(channel, carried) is not null ? _captureGains.GetValueOrDefault(channel.Id) : null;

    /// <summary>
    /// Read every capture input's gain again when its source's volume moved,
    /// the source came or went, or the inputs changed. True when a reported
    /// gain changed. Nothing is written.
    /// </summary>
    public bool SyncCaptureGains()
    {
        lock (_gate)
        {
            Dictionary<string, UnitInputStatus> carried = UnitInputsLocked();
            Dictionary<string, string> sources = _built
                ? _config.Channels.Select(c => (c.Id, Source: CaptureGainSource(c, carried)))
                    .Where(c => c.Source is not null).ToDictionary(c => c.Id, c => c.Source!, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            var names = new HashSet<string>(sources.Values, StringComparer.Ordinal);
            // The registry carries every node's channel volumes, so an
            // unchanged one costs no helper unless a failed hardware-range
            // probe is due again. pactl also knows the base volume.
            string? signature = names.Count == 0 ? "" : _pw.SourceVolumeSignature(names);
            string? key = signature is null ? null
                : string.Join('\n', sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}\t{s.Value}")) + "\n\n" + signature;
            if (key is not null && key == _captureGainKey
                && (_captureGainRetrySince is not { } since || Time.GetElapsedTime(since) < HardwareMinimumRetry)) return false;
            IReadOnlyDictionary<string, SourceVolume>? volumes = _pw.GetSourceVolumes(names);
            if (volumes is null) return false;   // pactl failed: keep the last reading and try again next sweep
            _captureGainKey = key;
            _captureGainRetrySince = null;
            var next = new Dictionary<string, CaptureGainStatus>(StringComparer.Ordinal);
            foreach ((string channel, string source) in sources)
                if (volumes.TryGetValue(source, out SourceVolume? volume))
                {
                    double? minimum = HardwareMinimumLocked(volume, out long? failedAt);
                    if (failedAt is { } failed && (_captureGainRetrySince is null || failed < _captureGainRetrySince.Value))
                        _captureGainRetrySince = failed;
                    next[channel] = CaptureGain.Status(volume, minimum);
                }
            if (next.Count == _captureGains.Count && next.All(g => _captureGains.GetValueOrDefault(g.Key) == g.Value)) return false;
            _captureGains.Clear();
            foreach ((string channel, CaptureGainStatus gain) in next) _captureGains[channel] = gain;
            return true;
        }
    }

    /// <summary>
    /// Set a capture input's gain in dB, clamped to its range: the device's
    /// capture control on a source that has one, its software level on any
    /// other. Refused on a channel whose gain belongs to a Wave unit, on one
    /// whose source OpenXLR never writes, and while the source is missing.
    /// </summary>
    public void SetCaptureGain(string channelId, double gainDb)
    {
        if (!double.IsFinite(gainDb)) throw new ArgumentException("setCaptureGain: the gain must be a finite number of dB.");
        lock (_gate)
        {
            ChannelDefinition channel = _config.Channels.FirstOrDefault(c => c.Id == channelId && c.CaptureSource is not null)
                ?? throw new ArgumentException($"'{channelId}' is not a capture input.");
            string source = CaptureGainSource(channel, UnitInputsLocked())
                ?? throw new InvalidOperationException(
                    $"{ChannelNameLocked(channel)} takes its sound from a Wave unit, whose gain is set through the unit's controls.");
            IReadOnlyDictionary<string, SourceVolume> volumes = _pw.GetSourceVolumes(new HashSet<string>([source], StringComparer.Ordinal))
                ?? throw new InvalidOperationException($"The volume of {source} could not be read.");
            if (!volumes.TryGetValue(source, out SourceVolume? volume))
                throw new InvalidOperationException($"{source} is not connected.");
            double? minimum = HardwareMinimumLocked(volume, out _);
            long raw = CaptureGain.RawFor(volume, CaptureGain.Status(volume, minimum), gainDb);
            _pw.SetSourceVolume(source, raw);
            // Report what was written at once; the next sweep reads it back.
            _captureGains[channelId] = CaptureGain.Status(volume with { Volume = raw }, minimum);
            _captureGainKey = null;
        }
    }

    /// <summary>
    /// The device's lowest hardware gain, read once per source from the ALSA
    /// capture control whose highest gain the base volume stands for; null
    /// for a software level, without a card or a matching control. Failed
    /// helper calls are retried after a bounded delay, even at unchanged volume.
    /// </summary>
    private double? HardwareMinimumLocked(SourceVolume volume, out long? failedAt)
    {
        failedAt = null;
        if (!CaptureGain.IsHardware(volume) || volume.AlsaCard is not { } card) return null;
        string key = $"{volume.Name}\t{card}\t{volume.BaseVolume}";
        if (_hardwareMinimums.TryGetValue(key, out var known)
            && (known.FailedAt is not { } previousFailure || Time.GetElapsedTime(previousFailure) < HardwareMinimumRetry))
        {
            failedAt = known.FailedAt;
            return known.Value;
        }
        string? controls = PipeWireAdapter.AlsaControls(card);
        double? found = controls is not null ? CaptureGain.HardwareMinimum(controls, -CaptureGain.VolumeDb(volume.BaseVolume)) : null;
        if (controls is null) failedAt = Time.GetTimestamp();
        if (_hardwareMinimums.Count >= 64) _hardwareMinimums.Clear();
        _hardwareMinimums[key] = (found, failedAt);
        return found;
    }
}
