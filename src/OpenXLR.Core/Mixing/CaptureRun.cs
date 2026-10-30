namespace OpenXLR.Core.Mixing;

/// <summary>
/// What the graph and the kernel say about one capture source: its node id,
/// whether it is an ALSA card's node and whether PipeWire runs it, and for
/// an ALSA card whether the kernel's capture stream runs. The two can disagree. A Wave:3 MK.2 whose playback
/// opened first kept its PipeWire source running while
/// <c>/proc/asound/MK2/stream0</c> read <c>Capture: Status: Stop</c> and no
/// data came, so a linked capture is not yet a running one.
/// </summary>
public readonly record struct CaptureRun(int? NodeId, bool Alsa, bool NodeRunning, bool? KernelRunning)
{
    /// <summary>The source is not in the graph.</summary>
    public static CaptureRun Missing => new(null, false, false, null);

    /// <summary>
    /// Whether the capture can be taken as started. A source missing from
    /// the graph is not. The kernel decides where the card's stream file is
    /// readable. Without it, an ALSA node counts when PipeWire runs it, and
    /// any other source (a virtual one, another program's) as soon as it is
    /// there and linked, as before the kernel was asked.
    /// </summary>
    public bool Started => NodeId is not null && (KernelRunning ?? (!Alsa || NodeRunning));

    /// <summary>
    /// The capture status in a USB audio card's stream file
    /// (<c>/proc/asound/cardN/streamM</c>): true for <c>Status: Running</c>,
    /// false for <c>Status: Stop</c>, null when the file has no capture
    /// section or does not say.
    /// </summary>
    internal static bool? ParseKernelStatus(string stream)
    {
        bool inCapture = false;
        foreach (string raw in stream.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            // A section header starts at the first column: "Playback:", "Capture:".
            if (!char.IsWhiteSpace(line[0]))
            {
                if (inCapture) return null;
                inCapture = line.Trim() == "Capture:";
                continue;
            }
            if (!inCapture) continue;
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("Status:", StringComparison.Ordinal)) continue;
            string status = trimmed["Status:".Length..].Trim();
            return status switch
            {
                "Running" => true,
                "Stop" => false,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>The kernel's capture status of one card's stream, or null when it cannot be read.</summary>
    internal static bool? ReadKernelStatus(string root, int card, int device)
    {
        try { return ParseKernelStatus(File.ReadAllText(Path.Combine(root, $"card{card}", $"stream{device}"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
