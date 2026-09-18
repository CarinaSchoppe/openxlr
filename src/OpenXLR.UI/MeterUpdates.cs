using System;
using System.Text.Json.Nodes;

namespace OpenXLR.UI;

/// <summary>
/// Retain the newest meter frame while the UI is busy. There is at most one
/// pending dispatcher callback, so resuming the window does not replay old
/// readings. State changes and command replies keep their normal ordering.
/// </summary>
internal sealed class MeterUpdates
{
    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private readonly Action<JsonNode> _apply;
    private readonly Action _drain;
    private JsonNode? _latest;
    private bool _queued;

    internal MeterUpdates(Action<Action> post, Action<JsonNode> apply)
    {
        _post = post;
        _apply = apply;
        _drain = Drain;
    }

    internal void Publish(JsonNode levels)
    {
        lock (_gate)
        {
            _latest = levels;
            if (_queued) return;
            _queued = true;
        }
        _post(_drain);
    }

    private void Drain()
    {
        JsonNode? levels;
        lock (_gate)
        {
            levels = _latest;
            _latest = null;
            _queued = false;
        }
        // Release the slot before applying: a frame arriving during the
        // update schedules the next turn instead of being left undelivered.
        if (levels is not null) _apply(levels);
    }
}
