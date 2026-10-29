using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>How far one clip has got: whether the player holds it, its length at the helper's rate, and why it is not held.</summary>
internal sealed record ClipLoad(bool Loaded, double Seconds, string? Error);

/// <summary>
/// One running soundboard player and what it holds: the slot each clip is
/// in, whether the clip loaded, and the loads still to do. Loads go to the
/// helper one at a time, each after the last one answered, because a load
/// decodes on the helper's main thread and holds its meter lines and its
/// heartbeat while it does. Presses never wait for a load or for the
/// mixer's lock: each one is a single line on the helper's pipe.
/// </summary>
internal sealed class SoundboardPlayer : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipLoad> _loads = new(StringComparer.Ordinal);
    private readonly Queue<(string Id, int Slot, string Path)> _queue = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _loadTimeout;
    private readonly TimeSpan _loadGap;
    private readonly Task _loader;
    // Presses are numbered in the order they reach the helper, so the
    // number the helper reports back as ended covers every earlier one.
    private readonly object _presses = new();
    private uint _lastPress;
    private TaskCompletionSource<ClipReply>? _pending;
    private int _pendingSlot = -1;
    private uint _pendingToken;
    private uint _nextToken;
    private volatile string? _loading;
    private volatile string? _stuck;
    private int _version;
    private int _disposed;

    public NativePluginHost Host { get; }

    /// <summary>
    /// The largest press number, the largest whole number a float meter
    /// holds exactly. The count starts over at 1 past it.
    /// </summary>
    internal const uint MaxPress = 16777215;

    /// <summary>The clip a load is waiting on, or null.</summary>
    public string? Loading => _loading;

    /// <summary>
    /// A clip whose load did not answer in time, or null. Its slot was
    /// unloaded in the helper, whose decoder may still be inside the file,
    /// so the owner starts another helper without that clip.
    /// </summary>
    public string? Stuck => _stuck;

    /// <summary>Counts every change to what the player holds, so a reader can tell that something moved.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <param name="clips">Each clip's id, its slot and the file to load, in the order to load them.</param>
    public SoundboardPlayer(NativePluginHost host, IEnumerable<(string Id, int Slot, string Path)> clips,
        TimeSpan? loadTimeout = null, TimeSpan? loadGap = null)
    {
        Host = host;
        _loadTimeout = loadTimeout ?? TimeSpan.FromSeconds(10);
        _loadGap = loadGap ?? TimeSpan.FromMilliseconds(20);
        foreach ((string id, int slot, string path) in clips)
        {
            if (slot is < 0 or >= SoundboardLibrary.MaxClips || _slots.ContainsValue(slot) || !_slots.TryAdd(id, slot))
                throw new ArgumentException("Every clip needs a slot of its own.", nameof(clips));
            _loads[id] = new ClipLoad(false, 0, null);
            _queue.Enqueue((id, slot, path));
            _work.Release();
        }
        host.ClipReplied += OnReply;
        _loader = Task.Run(LoadAsync);
    }

    /// <summary>The slot a clip is in, or null when the player does not have it.</summary>
    public int? SlotOf(string id)
    {
        lock (_lock) return _slots.TryGetValue(id, out int slot) ? slot : null;
    }

    public ClipLoad? Status(string id)
    {
        lock (_lock) return _loads.GetValueOrDefault(id);
    }

    /// <summary>
    /// Give a clip the lowest free slot, set its gain and loop there, and
    /// queue its load. Throws when every slot is taken.
    /// </summary>
    public int Add(string id, string path, double gain, bool loop)
    {
        int slot;
        lock (_lock)
        {
            if (_slots.ContainsKey(id)) throw new InvalidOperationException("The player already holds this clip.");
            slot = Enumerable.Range(0, SoundboardLibrary.MaxClips).FirstOrDefault(s => !_slots.ContainsValue(s), -1);
            if (slot < 0) throw new InvalidOperationException($"The player holds {SoundboardLibrary.MaxClips} clips already.");
            _slots[id] = slot;
            _loads[id] = new ClipLoad(false, 0, null);
        }
        Host.ForgetMeters(Playing(slot), Position(slot), Ended(slot));
        Host.SetControl(Gain(slot), gain);
        Host.SetControl(Loop(slot), loop ? 1 : 0);
        lock (_lock) _queue.Enqueue((id, slot, path));
        _work.Release();
        Interlocked.Increment(ref _version);
        return slot;
    }

    /// <summary>Fade the clip out, empty its slot and forget it.</summary>
    public void Remove(string id)
    {
        int slot;
        lock (_lock)
        {
            if (!_slots.Remove(id, out slot)) return;
            _loads.Remove(id);
        }
        Interlocked.Increment(ref _version);
        Host.ForgetMeters(Playing(slot), Position(slot), Ended(slot));
        Host.Send($"stop {slot}");
        Host.Send($"unload {slot}");
    }

    /// <summary>
    /// Press the clip's key: <paramref name="mode"/> is one of
    /// <see cref="SoundboardLibrary.Modes"/>. Refused while the clip has not
    /// loaded, with <paramref name="name"/> in the sentence. Returns the
    /// press's number, which <see cref="HoldEnded"/> takes.
    /// </summary>
    public uint Play(string id, string mode, string name)
    {
        if (!SoundboardLibrary.Modes.Contains(mode, StringComparer.Ordinal)) throw new ArgumentException("Unknown press mode.", nameof(mode));
        int slot;
        lock (_lock)
        {
            if (!_slots.TryGetValue(id, out slot) || _loads.GetValueOrDefault(id) is not { } load)
                throw new InvalidOperationException($"{name} is not in the player.");
            if (!load.Loaded)
                throw new InvalidOperationException(load.Error is null ? $"{name} is still loading." : $"{name} could not be loaded: {load.Error}");
        }
        lock (_presses)
        {
            uint press = _lastPress = _lastPress >= MaxPress ? 1 : _lastPress + 1;
            Host.Send($"play {slot} {mode} {press.ToString(CultureInfo.InvariantCulture)}");
            return press;
        }
    }

    /// <summary>
    /// Whether the voice a hold press started has ended, by a release, a
    /// stop, the clip's own end or a steal, or never started: the helper's
    /// <c>ended_N</c> meter names the newest press on the slot whose hold
    /// voice, and every older one's, is over. False until the helper has
    /// reported on the press, so a press it has not taken yet counts as
    /// playing. True for a clip the player no longer holds.
    /// </summary>
    public bool HoldEnded(string id, uint press)
    {
        if (SlotOf(id) is not int slot) return true;
        if (Host.Meter(Ended(slot)) is not double reported || reported < 1 || reported > MaxPress) return false;
        return Covers((uint)reported, press);
    }

    /// <summary>
    /// Whether press <paramref name="press"/> is at or before
    /// <paramref name="ended"/> in a count that starts over at 1 past
    /// <see cref="MaxPress"/>: within the half of the count behind it.
    /// </summary>
    internal static bool Covers(uint ended, uint press) => ((ended - press) & MaxPress) < (MaxPress + 1) / 2;

    /// <summary>The key came up: end the clip's hold voices.</summary>
    public void Release(string id) => SendFor(id, "release");

    /// <summary>Fade the clip's voices out.</summary>
    public void Stop(string id) => SendFor(id, "stop");

    /// <summary>Fade every voice out.</summary>
    public void StopAll() => Host.Send("stop all");

    public void SetGain(string id, double gain)
    {
        if (SlotOf(id) is int slot) Host.SetControl(Gain(slot), gain);
    }

    public void SetLoop(string id, bool loop)
    {
        if (SlotOf(id) is int slot) Host.SetControl(Loop(slot), loop ? 1 : 0);
    }

    private void SendFor(string id, string verb)
    {
        if (SlotOf(id) is int slot) Host.Send($"{verb} {slot}");
    }

    /// <summary>
    /// The clips playing now, each with how far its newest voice is, 0 to
    /// 1, and how many voices play it, read from the helper's meters.
    /// </summary>
    public IReadOnlyDictionary<string, (double Position, int Voices)> Progress()
    {
        IReadOnlyDictionary<string, double> meters = Host.Meters;
        var playing = new Dictionary<string, (double, int)>(StringComparer.Ordinal);
        lock (_lock)
            foreach ((string id, int slot) in _slots)
            {
                int voices = (int)Math.Clamp(meters.GetValueOrDefault(Playing(slot)), 0, SoundboardLibrary.MaxVoices);
                if (voices > 0) playing[id] = (Math.Round(Math.Clamp(meters.GetValueOrDefault(Position(slot)), 0, 1), 3), voices);
            }
        return playing;
    }

    internal static string Gain(int slot) => "gain_" + slot.ToString(CultureInfo.InvariantCulture);
    internal static string Loop(int slot) => "loop_" + slot.ToString(CultureInfo.InvariantCulture);
    internal static string Playing(int slot) => "playing_" + slot.ToString(CultureInfo.InvariantCulture);
    internal static string Position(int slot) => "pos_" + slot.ToString(CultureInfo.InvariantCulture);
    internal static string Ended(int slot) => "ended_" + slot.ToString(CultureInfo.InvariantCulture);

    private void OnReply(ClipReply reply)
    {
        if (reply.Outcome == ClipOutcome.Unloaded) return;
        // Only the answer to the load now waiting counts: one to a load that
        // timed out names another token and is dropped.
        TaskCompletionSource<ClipReply>? pending;
        lock (_lock) pending = reply.Slot == _pendingSlot && reply.Token == _pendingToken ? _pending : null;
        pending?.TrySetResult(reply);
    }

    private async Task LoadAsync()
    {
        try
        {
            while (true)
            {
                await _work.WaitAsync(_stop.Token).ConfigureAwait(false);
                // Once a load is stuck the helper is to be replaced; nothing
                // more is sent to it.
                if (_stuck is not null) continue;
                (string Id, int Slot, string Path) item;
                var reply = new TaskCompletionSource<ClipReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                uint token;
                lock (_lock)
                {
                    if (!_queue.TryDequeue(out item)) continue;
                    // A clip removed while it waited is not loaded at all.
                    if (_slots.GetValueOrDefault(item.Id, -1) != item.Slot) continue;
                    token = _nextToken = _nextToken == uint.MaxValue ? 1 : _nextToken + 1;
                    _pending = reply;
                    _pendingSlot = item.Slot;
                    _pendingToken = token;
                    _loading = item.Id;
                }
                ClipLoad outcome;
                try
                {
                    Host.Send($"load {item.Slot} {token.ToString(CultureInfo.InvariantCulture)} {item.Path}");
                    ClipReply answer = await reply.Task.WaitAsync(_loadTimeout, _stop.Token).ConfigureAwait(false);
                    outcome = answer.Outcome == ClipOutcome.Loaded
                        ? new ClipLoad(true, Host.SampleRate > 0 ? (double)answer.Frames / Host.SampleRate : 0, null)
                        : new ClipLoad(false, 0, Sentence(answer.Message));
                }
                catch (TimeoutException)
                {
                    outcome = new ClipLoad(false, 0, "The player did not load the clip in time.");
                    // The helper drops the load if it ever comes back, and is
                    // replaced: its decoder may never come back at all.
                    try { Host.Send($"unload {item.Slot}"); }
                    catch (InvalidOperationException) { }
                    _stuck = item.Id;
                }
                catch (ArgumentException) { outcome = new ClipLoad(false, 0, "The clip's path cannot be handed to the player."); }
                catch (InvalidOperationException) { outcome = new ClipLoad(false, 0, "The player stopped before the clip loaded."); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { outcome = new ClipLoad(false, 0, $"The clip could not be loaded: {ex.Message}"); }
                bool orphaned;
                lock (_lock)
                {
                    _pending = null;
                    _pendingSlot = -1;
                    _loading = null;
                    orphaned = _slots.GetValueOrDefault(item.Id, -1) != item.Slot;
                    if (!orphaned) _loads[item.Id] = outcome;
                }
                Interlocked.Increment(ref _version);
                // Removed while it loaded: the slot is emptied again, unless
                // another clip has taken it since and is queued behind.
                if (orphaned && outcome.Loaded && SlotFree(item.Slot))
                {
                    try { Host.Send($"unload {item.Slot}"); }
                    catch (InvalidOperationException) { }
                }
                await Task.Delay(_loadGap, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private bool SlotFree(int slot)
    {
        lock (_lock) return !_slots.ContainsValue(slot);
    }

    /// <summary>The helper's message as a sentence: a capital first letter and one full stop.</summary>
    internal static string Sentence(string? message)
    {
        string text = (message ?? "").Trim().TrimEnd('.');
        return text.Length == 0 ? "The clip could not be loaded." : char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    /// <summary>Stop loading. The helper itself is stopped by its owner.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Host.ClipReplied -= OnReply;
        _stop.Cancel();
        try { _loader.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _stop.Dispose();
        _work.Dispose();
    }
}
