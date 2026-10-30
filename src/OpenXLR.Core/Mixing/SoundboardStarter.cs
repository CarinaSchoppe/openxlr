using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>An import of starter sounds that could not start: the set is not installed, or cannot be read.</summary>
public sealed class StarterSoundsMissingException(string message) : InvalidOperationException(message);

/// <summary>
/// The starter sounds: a few short CC0 clips that ship with OpenXLR, laid out
/// as a soundboard archive is, but as a folder: <c>soundboard.json</c> lists
/// them, each with a starter id, and <c>clips/</c> holds their files. They are
/// added to a library once by themselves, and again by hand for the ones the
/// library no longer has.
/// </summary>
public static class StarterSounds
{
    /// <summary>The folder the packages install the set in: <c>starter-sounds</c> beside the daemon.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "starter-sounds");

    /// <summary>
    /// The starter clips the folder's <c>soundboard.json</c> lists, in order:
    /// the clips that keep the library's rules and have a starter id. A clip
    /// that breaks a rule is named in <paramref name="skipped"/>. A folder
    /// without a readable library file is refused.
    /// </summary>
    public static List<SoundboardClip> Read(string directory, List<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        string library = Path.Combine(directory, SoundboardArchive.LibraryEntry);
        string text;
        try { text = RegularFile.ReadText(library, OpenXlrPaths.MaxPreferenceBytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StarterSoundsMissingException($"The starter sounds are not installed: {library} cannot be read ({ex.Message}).");
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new StarterSoundsMissingException($"The starter sounds' {SoundboardArchive.LibraryEntry} holds no object.");
            var dropped = new List<string>();
            List<SoundboardClip> clips = SoundboardLibrary.ReadClips(document.RootElement, dropped);
            skipped.AddRange(dropped.Select(d => $"{d}: the entry breaks the library's rules"));
            return [.. clips.Where(c => c.Starter.Length > 0)];
        }
        catch (JsonException ex)
        {
            throw new StarterSoundsMissingException($"The starter sounds' {SoundboardArchive.LibraryEntry} is not valid JSON: {ex.Message}");
        }
    }
}

public sealed partial class Mixer
{
    /// <summary>
    /// Add the starter sounds once, when this library has never had them:
    /// every starter clip it lacks, after the clips here, which are kept.
    /// The library then says they were added, in the same save, so they are
    /// never added by themselves again. Returns null when they were added
    /// before. Throws as <see cref="RestoreStarterSounds"/> does, and then
    /// the library still says they were not added, for the next start.
    /// </summary>
    public SoundboardImport? AddStarterSoundsOnce(string directory)
    {
        if (_board.StarterAdded) return null;
        return AddStarterSounds(directory, once: true);
    }

    /// <summary>
    /// Add back each starter clip the library no longer has, after the clips
    /// here, which are kept. A starter clip counts as present by its starter
    /// id, whatever it is named now and on whichever page. Each one is copied
    /// in and checked by the helper as an archive's clip is; one that fails
    /// is left out and named. Returns what was added, nothing when no starter
    /// clip is missing. Throws <see cref="StarterSoundsMissingException"/>
    /// when the set is not installed, and refuses, with nothing changed,
    /// without the helper, while another library transfer runs, or when no
    /// missing clip can be added.
    /// </summary>
    public SoundboardImport RestoreStarterSounds(string directory) => AddStarterSounds(directory, once: false);

    private SoundboardImport AddStarterSounds(string directory, bool once)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!_pw.PlayerHostInstalled) throw new InvalidOperationException(SoundboardNeedsHost);
        if (!_boardTransfer.Wait(0)) throw new InvalidOperationException(SoundboardTransferBusy);
        try
        {
            var skipped = new List<string>();
            List<SoundboardClip> set = StarterSounds.Read(directory, skipped);
            if (set.Count == 0) throw new StarterSoundsMissingException("The starter sounds' library lists no clip.");
            List<SoundboardClip> missing;
            lock (_gate)
            {
                if (once && _board.StarterAdded) return new SoundboardImport([], [], new Dictionary<string, string>(), false);
                var present = new HashSet<string>(_board.Clips.Select(c => c.Starter).Where(s => s.Length > 0), StringComparer.Ordinal);
                missing = [.. set.Where(c => !present.Contains(c.Starter))];
                if (missing.Count == 0)
                {
                    if (!_board.StarterAdded) SaveBoardLocked(_board with { StarterAdded = true });
                    return new SoundboardImport([], skipped, new Dictionary<string, string>(), false);
                }
            }
            string clips = Path.Combine(directory, "clips");
            return ImportClips(missing, skipped, merge: true, "starter sounds",
                clip => staging => CopyClipIn(Path.Combine(clips, clip.File), staging), starter: true);
        }
        finally
        {
            _boardTransfer.Release();
        }
    }
}
