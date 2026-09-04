using System.Threading.Channels;

namespace OpenXLR.Daemon;

/// <summary>
/// Carries a dirty flag, not a snapshot: bursts retain at most one pending
/// update and event producers never acquire the device or mixer locks.
/// </summary>
internal sealed class StateBroadcastQueue(Action broadcast, ILogger log)
{
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    internal void Signal() => _changes.Writer.TryWrite(true);

    internal async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await foreach (bool ignored in _changes.Reader.ReadAllAsync(stop))
            {
                if (stop.IsCancellationRequested) break;
                try { broadcast(); }
                catch (Exception ex) { log.LogWarning(ex, "State broadcast failed"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
