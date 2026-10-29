using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The soundboard as clients see it: the channel its clips play into (null
/// once that channel was deleted), whether it is on, whether this build has
/// the helper it needs, the last failure or notice, the formats this build
/// decodes, the clips in library order and the ones playing now.
/// </summary>
public sealed record SoundboardState(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Channel,
    bool Enabled,
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error,
    IReadOnlyList<string> Formats,
    IReadOnlyList<SoundboardClipState> Clips,
    IReadOnlyList<string> Playing)
{
    public static SoundboardState Off { get; } = new(null, false, false, null, [], [], []);
}

/// <summary>One clip as the state publishes it: the library entry, whether the player holds it, why not, and whether it plays.</summary>
public sealed record SoundboardClipState(
    string Id,
    string Name,
    double Seconds,
    double Gain,
    string Mode,
    bool Loop,
    string Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Colour,
    bool Loaded,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error,
    bool Playing);

/// <summary>What <c>updateClip</c> changes; a null field is kept. An empty colour clears it.</summary>
public sealed record SoundboardClipChange(string? Name = null, double? Gain = null, string? Mode = null, bool? Loop = null,
    string? Icon = null, string? Colour = null);

public sealed partial class Mixer
{
    /// <summary>The channel the soundboard plays into, by id: renaming it keeps the board's binding.</summary>
    public const string SoundboardChannel = "sfx";

    /// <summary>The name a recreated SFX channel gets, from which its id is made.</summary>
    internal const string SoundboardChannelName = "SFX";

    /// <summary>What the soundboard refuses with when this build has no native helper.</summary>
    public const string SoundboardNeedsHost =
        "The soundboard needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What the state says after the SFX channel was deleted while the board was on.</summary>
    public const string SoundboardChannelDeleted =
        "The soundboard is off because the SFX channel was deleted. Turn it on again to create SFX, muted in every mix.";

    /// <summary>What the state says when the board is on in the file but the layout has no SFX channel.</summary>
    internal const string SoundboardChannelMissing =
        "The soundboard has no SFX channel to play into. Turn it on again to create SFX, muted in every mix.";

    private const string SoundboardKey = "soundboard";

    // The library is replaced whole, never changed in place, so a press can
    // read it without the mixer's lock.
    private volatile SoundboardLibrary _board = SoundboardLibrary.Empty;
    private Func<SoundboardLibrary, string?> _boardSave = library => library.Save();
    private string _clipDirectory = SoundboardLibrary.ClipDirectory;
    private string? _boardError;
    private FilterHandle? _playerFilter;
    private PortLink? _playerLink;
    // Read by presses and the meter push without the mixer's lock.
    private volatile SoundboardPlayer? _player;
    // Clips the player died while loading. A file that crashes the decoder
    // is not given to the next player, which would die the same way.
    private readonly Dictionary<string, string> _crashedClips = new(StringComparer.Ordinal);
    private string? _lastBoardState;
    private string _lastBoardActivity = "";

    /// <summary>
    /// Take the library read at start, where it is saved, and where its clip
    /// files live, and start the player when the library says so. Called
    /// once the graph is built; a later rebuild starts the player again by
    /// itself.
    /// </summary>
    public void UseSoundboard(SoundboardLibrary library, Func<SoundboardLibrary, string?>? save = null, string? clipDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        lock (_gate)
        {
            StopPlayerLocked();
            _board = library;
            if (save is not null) _boardSave = save;
            if (clipDirectory is not null) _clipDirectory = clipDirectory;
            _boardError = null;
            if (_built) StartSoundboardIfWantedLocked();
        }
    }

    /// <summary>The library as it is now.</summary>
    public SoundboardLibrary SoundboardLibrary => _board;

    /// <summary>The soundboard's part of the state.</summary>
    public SoundboardState Soundboard()
    {
        lock (_gate) return SoundboardSnapshotLocked();
    }

    private ChannelDefinition? SoundboardTargetLocked()
        => _config.Channels.FirstOrDefault(c => c.Id == SoundboardChannel && c.IsApplication);

    private SoundboardState SoundboardSnapshotLocked()
    {
        SoundboardPlayer? player = _player;
        IReadOnlyDictionary<string, (double Position, int Voices)> playing = player?.Progress()
            ?? new Dictionary<string, (double, int)>();
        SoundboardLibrary board = _board;
        bool available = _pw.PlayerHostInstalled;
        return new SoundboardState(
            SoundboardTargetLocked()?.Id,
            board.Enabled,
            available,
            _boardError,
            available ? _pw.PlayerFormats : [],
            [.. board.Clips.Select(c =>
            {
                ClipLoad? load = player?.Status(c.Id);
                return new SoundboardClipState(c.Id, c.Name, Math.Round(load is { Loaded: true } ? load.Seconds : c.Seconds, 2),
                    c.Gain, c.Mode, c.Loop, c.Icon, c.Colour, load?.Loaded == true,
                    _crashedClips.GetValueOrDefault(c.Id) ?? load?.Error, playing.ContainsKey(c.Id));
            })],
            [.. board.Clips.Where(c => playing.ContainsKey(c.Id)).Select(c => c.Id)]);
    }

    /// <summary>
    /// Turn the board on or off, and save that. On creates an application
    /// channel named SFX first when the layout has none with the id
    /// <c>sfx</c>; it starts muted in every mix, as every created channel
    /// does. An application channel that already holds the id is used as it
    /// is. <paramref name="persist"/> saves the layout when SFX is created.
    /// </summary>
    public void SetSoundboard(bool enabled, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!enabled)
            {
                StopPlayerLocked();
                SaveBoardLocked(_board with { Enabled = false });
                _boardError = null;
                _restarts.Forget(SoundboardKey);
                return;
            }
            if (!_pw.PlayerHostInstalled) throw new InvalidOperationException(SoundboardNeedsHost);
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (_config.Channels.FirstOrDefault(c => c.Id == SoundboardChannel) is { IsApplication: false })
                throw new InvalidOperationException("The soundboard plays into the channel with the id sfx, and that channel is an input here.");
            if (SoundboardTargetLocked() is null)
            {
                CreateApplicationChannel(SoundboardChannelName, persist);
                if (SoundboardTargetLocked() is null)
                    throw new InvalidOperationException("SFX was created under another id, so the soundboard has no channel to play into.");
            }
            SoundboardLibrary previous = _board;
            SaveBoardLocked(_board with { Enabled = true });
            _boardError = null;
            _restarts.Forget(SoundboardKey);
            if (_player is not null) return;
            try { StartPlayerLocked(); }
            catch (Exception ex)
            {
                _boardError = ex.Message;
                try { SaveBoardLocked(previous); }
                catch (IOException) { _board = previous; }
                throw;
            }
        }
    }

    /// <summary>
    /// Copy a file the user picked into the clip directory and add it to the
    /// library under an id made from <paramref name="name"/>, or from the
    /// file's name. The copy is checked by the helper in a process of its
    /// own before anything else reads it, so a file that crashes the decoder
    /// never reaches the running player. Returns the new clip.
    /// </summary>
    public SoundboardClip ImportClip(string path, string? name)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Give the clip's file as an absolute path.");
        if (!_pw.PlayerHostInstalled) throw new InvalidOperationException(SoundboardNeedsHost);
        string clipName = ClipName(name, path);
        string directory;
        lock (_gate)
        {
            if (_board.Clips.Count >= SoundboardLibrary.MaxClips)
                throw new InvalidOperationException($"The soundboard holds {SoundboardLibrary.MaxClips} clips already. Delete one first.");
            directory = _clipDirectory;
        }
        OpenXlrPaths.EnsurePrivateDir(directory);
        string staging = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CopyClipIn(path, staging);
            // The copy is what is checked and what the player reads, so a file
            // changed after this check is never the one played.
            ClipProbe probe = _pw.ProbeClip(staging);
            if (probe.Seconds > SoundboardLibrary.MaxSeconds)
                throw new InvalidOperationException($"The clip is {probe.Seconds:0.0} s long; the limit is {SoundboardLibrary.MaxSeconds} s.");
            lock (_gate)
            {
                if (_board.Clips.Count >= SoundboardLibrary.MaxClips)
                    throw new InvalidOperationException($"The soundboard holds {SoundboardLibrary.MaxClips} clips already. Delete one first.");
                if (directory != _clipDirectory) throw new InvalidOperationException("The clip directory changed; import the file again.");
                string id = SoundboardLibrary.NewId(clipName, _board.Clips.Select(c => c.Id));
                var clip = new SoundboardClip
                {
                    Id = id,
                    Name = clipName,
                    File = $"{id}.{probe.Extension}",
                    Seconds = Math.Round(probe.Seconds, 3),
                };
                string target = Path.Combine(directory, clip.File);
                // A file under this name that the library does not list is a
                // leftover of an earlier import; rename replaces it, link or not.
                File.Move(staging, target, overwrite: true);
                try { SaveBoardLocked(_board with { Clips = [.. _board.Clips, clip] }); }
                catch
                {
                    TryDelete(target);
                    throw;
                }
                _crashedClips.Remove(id);
                try { _player?.Add(clip.Id, target, clip.Gain, clip.Loop); }
                catch (InvalidOperationException ex) { _boardError = $"{clip.Name} was imported, but the player did not take it: {ex.Message}"; }
                return clip;
            }
        }
        finally { TryDelete(staging); }
    }

    /// <summary>The name a clip is imported under: the one given, else the file's name without its extension.</summary>
    internal static string ClipName(string? name, string path)
    {
        string text = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name;
        text = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        if (text.Length > SoundboardLibrary.MaxNameLength) text = text[..SoundboardLibrary.MaxNameLength].TrimEnd();
        if (text.Length == 0) text = "Clip";
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != text)
            throw new InvalidOperationException("The clip's name must have 1 to 60 printable characters.");
        return text;
    }

    /// <summary>
    /// Copy a regular file of at most 64 MiB into a new private file. The
    /// source is opened once, following a link, and checked through that
    /// handle, so what is copied is what was checked.
    /// </summary>
    internal static void CopyClipIn(string source, string target)
    {
        // A dangling link is a file that does not exist, too.
        if (!Path.Exists(source)) throw new InvalidOperationException("The file does not exist.");
        FileStream input;
        try
        {
            RegularFile.Require(source);
            input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(ex is FileNotFoundException or DirectoryNotFoundException
                ? "The file does not exist." : ex is UnauthorizedAccessException ? "The file cannot be read." : "The path is not a regular file.", ex);
        }
        using (input)
        {
            // The path was a regular file a moment ago; the handle is checked
            // too, in case the name was pointed elsewhere in between.
            if (OperatingSystem.IsLinux()
                && RegularFile.Inspect($"/proc/self/fd/{(long)input.SafeFileHandle.DangerousGetHandle()}") is { } status
                && (status.Mode & 0xf000) != 0x8000)
                throw new InvalidOperationException("The path is not a regular file.");
            if (input.Length > SoundboardLibrary.MaxFileBytes)
                throw new InvalidOperationException($"The file is {input.Length / 1048576.0:0.0} MiB; the limit is {SoundboardLibrary.MaxFileBytes >> 20} MiB.");
            using FileStream output = OpenXlrPaths.CreatePrivate(target);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                copied += read;
                if (copied > SoundboardLibrary.MaxFileBytes)
                    throw new InvalidOperationException($"The file is larger than {SoundboardLibrary.MaxFileBytes >> 20} MiB.");
                output.Write(buffer, 0, read);
            }
            if (copied == 0) throw new InvalidOperationException("The file is empty.");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Change a clip's name, gain, press mode, loop, icon or colour. Gain and loop reach a playing clip at once.</summary>
    public void UpdateClip(string id, SoundboardClipChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            SoundboardClip clip = BoardClip(id);
            SoundboardClip next = clip with
            {
                Name = change.Name?.Trim() ?? clip.Name,
                Gain = change.Gain ?? clip.Gain,
                Mode = change.Mode ?? clip.Mode,
                Loop = change.Loop ?? clip.Loop,
                Icon = change.Icon ?? clip.Icon,
                Colour = change.Colour is null ? clip.Colour : change.Colour.Length == 0 ? null : change.Colour,
            };
            if (SoundboardLibrary.Problem(next) is { } problem)
                throw new InvalidOperationException(char.ToUpperInvariant(problem[0]) + problem[1..] + ".");
            SaveBoardLocked(_board with { Clips = [.. _board.Clips.Select(c => c.Id == id ? next : c)] });
            if (_player is { } player)
            {
                if (next.Gain != clip.Gain) player.SetGain(id, next.Gain);
                if (next.Loop != clip.Loop) player.SetLoop(id, next.Loop);
            }
        }
    }

    /// <summary>Stop a clip, forget it and delete its file.</summary>
    public void DeleteClip(string id)
    {
        lock (_gate)
        {
            SoundboardClip clip = BoardClip(id);
            SaveBoardLocked(_board with { Clips = [.. _board.Clips.Where(c => c.Id != id)] });
            _crashedClips.Remove(id);
            try { _player?.Remove(id); }
            catch (InvalidOperationException) { /* a player that cannot take the line is restarted without the clip */ }
            string file = Path.Combine(_clipDirectory, clip.File);
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pw.Note($"The soundboard clip file {file} could not be deleted: {ex.Message}");
            }
        }
    }

    /// <summary>Put the clips in the order clients draw them; <paramref name="ids"/> names every clip once.</summary>
    public void SetClipOrder(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            if (ids.Count != _board.Clips.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
                || ids.Any(id => _board.Clips.All(c => c.Id != id)))
                throw new InvalidOperationException("Name every clip once.");
            SaveBoardLocked(_board with { Clips = [.. ids.Select(id => _board.Clips.First(c => c.Id == id))] });
        }
    }

    /// <summary>
    /// Press a clip's key: its own mode unless <paramref name="mode"/> gives
    /// another. Refused while the board is off, SFX is missing or the clip
    /// has not loaded. Never waits for the mixer's lock.
    /// </summary>
    public void PlayClip(string id, string? mode)
    {
        SoundboardClip clip = BoardClip(id);
        LivePlayer().Play(id, mode ?? clip.Mode, clip.Name);
    }

    /// <summary>The key came up: end the clip's hold voices.</summary>
    public void ReleaseClip(string id)
    {
        BoardClip(id);
        _player?.Release(id);
    }

    /// <summary>Fade the clip's voices out.</summary>
    public void StopClip(string id)
    {
        BoardClip(id);
        _player?.Stop(id);
    }

    /// <summary>Fade every voice out. Nothing plays while the board is off, so that is no error.</summary>
    public void StopClips() => _player?.StopAll();

    private SoundboardClip BoardClip(string id)
        => _board.Clips.FirstOrDefault(c => c.Id == id) ?? throw new InvalidOperationException($"No clip has the id {id}.");

    private SoundboardPlayer LivePlayer()
    {
        if (_player is { } player) return player;
        SoundboardLibrary board = _board;
        string? error = Volatile.Read(ref _boardError);
        throw new InvalidOperationException(
            !board.Enabled ? error ?? "The soundboard is off. Turn it on first."
            : error is not null ? $"The soundboard is not playing. {error}" : "The soundboard is starting.");
    }

    private void SaveBoardLocked(SoundboardLibrary next)
    {
        if (_boardSave(next) is string error) throw new IOException($"The soundboard library could not be saved: {error}");
        _board = next;
    }

    /// <summary>
    /// Start the player and link it into SFX. The link is made before any
    /// load or press is sent: an unlinked player never runs, and a press
    /// queued then would play late, all at once.
    /// </summary>
    private void StartPlayerLocked()
    {
        ChannelDefinition target = SoundboardTargetLocked() ?? throw new InvalidOperationException(SoundboardChannelMissing);
        List<SoundboardClip> clips = [.. _board.Clips.Where(c => !_crashedClips.ContainsKey(c.Id))];
        var controls = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int slot = 0; slot < clips.Count; slot++)
        {
            controls[SoundboardPlayer.Gain(slot)] = clips[slot].Gain;
            controls[SoundboardPlayer.Loop(slot)] = clips[slot].Loop ? 1 : 0;
        }
        FilterHandle filter = _pw.CreatePlayer(controls, out _);
        PortLink? link = null;
        try
        {
            link = _pw.LinkNodes(PipeWireAdapter.PlayerNode, "capture", target.SinkName, "playback");
            if (!link.Complete || link.Pairs.Count < 2)
                throw new InvalidOperationException("The soundboard player could not be connected to SFX.");
        }
        catch
        {
            if (link is not null) _pw.Unlink(link);
            _pw.StopFilter(filter);
            throw;
        }
        _playerFilter = filter;
        _playerLink = link;
        string directory = _clipDirectory;
        _player = new SoundboardPlayer(filter.NativeHost!, clips.Select((c, slot) => (c.Id, slot, Path.Combine(directory, c.File))));
        _boardError = null;
    }

    private void StopPlayerLocked()
    {
        SoundboardPlayer? player = _player;
        _player = null;
        player?.Dispose();
        // Unlinked first: a node with no link is not run, so it falls silent
        // at once, whatever the helper is still doing.
        if (_playerLink is not null) _pw.Unlink(_playerLink);
        _playerLink = null;
        if (_playerFilter is not null) _pw.StopFilter(_playerFilter);
        _playerFilter = null;
    }

    /// <summary>Start the player when the library wants it and nothing stands in the way; a failure is the state's error.</summary>
    private void StartSoundboardIfWantedLocked()
    {
        if (!_built || !_board.Enabled || _player is not null) return;
        if (SoundboardTargetLocked() is null) { _boardError ??= SoundboardChannelMissing; return; }
        if (!_pw.PlayerHostInstalled) { _boardError = SoundboardNeedsHost; return; }
        if (_restarts.Blocked(SoundboardKey)) return;
        try { StartPlayerLocked(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _boardError = ex.Message;
            _restarts.Failed(SoundboardKey, ex.Message);
            _pw.Note($"The soundboard player did not start: {ex.Message}");
        }
    }

    /// <summary>Link the player into SFX again, after SFX's sink was reloaded under a new name.</summary>
    private void RelinkPlayerLocked()
    {
        if (_player is null || SoundboardTargetLocked() is not { } target) return;
        if (_playerLink is not null) _pw.Unlink(_playerLink);
        _playerLink = _pw.LinkNodes(PipeWireAdapter.PlayerNode, "capture", target.SinkName, "playback");
    }

    /// <summary>
    /// SFX was deleted: the player stops and is unlinked, and the board is
    /// off with a notice. The library and the clip files stay.
    /// </summary>
    private void SoundboardChannelDeletedLocked()
    {
        bool wasOn = _board.Enabled || _player is not null;
        StopPlayerLocked();
        if (!wasOn) return;
        _boardError = SoundboardChannelDeleted;
        _restarts.Forget(SoundboardKey);
        SoundboardLibrary off = _board with { Enabled = false };
        if (_boardSave(off) is string error) _pw.Note($"The soundboard library could not be saved: {error}");
        _board = off;
    }

    /// <summary>
    /// The sweep's part: a player that died is started again, without the
    /// clip it died loading, until it has died three times in five minutes;
    /// a link that broke is made again. True when what clients see changed.
    /// </summary>
    private bool EnsureSoundboardLocked()
    {
        if (_player is { } player && _playerFilter is { } filter)
        {
            if (!filter.IsAlive)
            {
                string? words = filter.LastWords;
                string? loading = player.Loading;
                StopPlayerLocked();
                if (loading is not null && _board.Clips.FirstOrDefault(c => c.Id == loading) is { } culprit)
                    _crashedClips[loading] = $"The player stopped while it loaded {culprit.Name}, so it is left out. Import the file again once it plays elsewhere.";
                _restarts.Failed(SoundboardKey, words);
                _pw.Note($"The soundboard player stopped{(words is null ? "." : $". Its last message: {words}")}");
                if (_restarts.Blocked(SoundboardKey))
                {
                    _boardError = _restarts.Message(SoundboardKey, "The soundboard player kept failing and is off; turn the soundboard on to try again");
                    SoundboardLibrary off = _board with { Enabled = false };
                    if (_boardSave(off) is string error) _pw.Note($"The soundboard library could not be saved: {error}");
                    _board = off;
                }
                else _boardError = "The soundboard player stopped and was started again.";
            }
            else if (_playerLink is null || _pw.EnsureLinks(_playerLink) == LinkHealth.Broken)
                RelinkPlayerLocked();
        }
        if (_player is null && _board.Enabled)
        {
            string? before = _boardError;
            StartSoundboardIfWantedLocked();
            // A restart after a death keeps its notice until the next change.
            if (_player is not null && before is not null && before.EndsWith("started again.", StringComparison.Ordinal)) _boardError = before;
        }
        string state = JsonSerializer.Serialize(SoundboardSnapshotLocked());
        bool changed = state != _lastBoardState;
        _lastBoardState = state;
        return changed;
    }

    /// <summary>
    /// Whether a clip started, ended or finished loading since the last
    /// call, for the meter push: a clip that ends by itself pushes the state
    /// at once, not at the next sweep. Never takes the mixer's lock.
    /// </summary>
    public bool SoundboardActivityChanged()
    {
        string activity;
        if (_player is not { } player) activity = "";
        else
        {
            IReadOnlyDictionary<string, (double Position, int Voices)> playing = player.Progress();
            activity = player.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + string.Join(",", playing.Keys.Order(StringComparer.Ordinal));
        }
        if (activity == Volatile.Read(ref _lastBoardActivity)) return false;
        Volatile.Write(ref _lastBoardActivity, activity);
        return true;
    }

    /// <summary>
    /// Where each playing clip is, for the meter push: the key is
    /// <c>clip:</c> and the clip's id, the values its position, 0 to 1, and
    /// how many voices play it. Nothing for a clip that is not playing.
    /// </summary>
    private void AddClipPositions(Dictionary<string, double[]> meters)
    {
        if (_player is not { } player || !player.Host.IsRunning) return;
        foreach ((string id, (double position, int voices)) in player.Progress())
            meters[$"clip:{id}"] = [position, voices];
    }
}
