using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One clip of the soundboard as <c>soundboard.json</c> keeps it. The id is
/// made from the name when the clip is imported and never changes, so a Deck
/// key or a desktop key that names it keeps working after a rename. The file
/// is a bare name inside <see cref="SoundboardLibrary.ClipDirectory"/>, never
/// a path.
/// </summary>
public sealed record SoundboardClip
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string File { get; init; }
    /// <summary>The clip's own gain, 0 to 1, unity at 1.</summary>
    public double Gain { get; init; } = 1;
    /// <summary>What a press does while the clip plays: one of <see cref="SoundboardLibrary.Modes"/>.</summary>
    public string Mode { get; init; } = SoundboardLibrary.DefaultMode;
    /// <summary>Start over at the end instead of stopping.</summary>
    public bool Loop { get; init; }
    /// <summary>The pad's and the Deck key's icon, as the layout's appearance takes it.</summary>
    public string Icon { get; init; } = "";
    /// <summary>#RRGGBB, or null for the client's own.</summary>
    public string? Colour { get; init; }
    /// <summary>The clip's length, as the helper measured it when the clip was imported.</summary>
    public double Seconds { get; init; }
    /// <summary>The page clients draw the clip on, or empty for the default page.</summary>
    public string Page { get; init; } = "";
}

/// <summary>
/// The soundboard's clip library: whether the player starts with the
/// daemon, and the clips in the order clients draw them. Machine data like
/// the input bindings: profiles never carry it. Read once at start, written
/// before every change is acknowledged.
/// </summary>
public sealed record SoundboardLibrary
{
    /// <summary>The most clips a library holds; the player has one slot for each.</summary>
    public const int MaxClips = 64;
    /// <summary>The most clips heard at once; one more press fades out the oldest.</summary>
    public const int MaxVoices = 8;
    /// <summary>The longest clip, in seconds.</summary>
    public const int MaxSeconds = 30;
    /// <summary>The largest clip file, in bytes.</summary>
    public const long MaxFileBytes = 64L << 20;
    /// <summary>The longest file name a clip may have, in UTF-8 bytes.</summary>
    public const int MaxFileNameBytes = 255;
    /// <summary>The longest clip id.</summary>
    public const int MaxIdLength = 40;
    /// <summary>The longest clip name, in characters.</summary>
    public const int MaxNameLength = 60;
    /// <summary>The longest page name, in characters.</summary>
    public const int MaxPageLength = 40;

    /// <summary>The press behaviours, in the player's words.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["overlap", "restart", "toggle", "hold"];
    public const string DefaultMode = "overlap";

    /// <summary>
    /// The extensions a copied clip is stored under, named after the format
    /// the helper found in it: the five the soundboard offers, AIFF, and
    /// <c>audio</c> for anything else this build's decoder reads.
    /// </summary>
    public static IReadOnlyList<string> Extensions { get; } = ["wav", "flac", "ogg", "opus", "mp3", "aiff", "audio"];

    public bool Enabled { get; init; }
    public IReadOnlyList<SoundboardClip> Clips { get; init; } = [];

    /// <summary>
    /// The ids of deleted clips, never given to a new clip, so a Deck key or
    /// a desktop key still bound to one never plays another clip. The oldest
    /// go first past <see cref="MaxRetired"/>.
    /// </summary>
    public IReadOnlyList<string> Retired { get; init; } = [];

    /// <summary>The most retired ids a library keeps.</summary>
    public const int MaxRetired = 1024;

    /// <summary>The retired ids with <paramref name="id"/> added, the oldest dropped past the limit.</summary>
    public static IReadOnlyList<string> Retire(IReadOnlyList<string> retired, string id)
        => [.. retired.Where(r => !string.Equals(r, id, StringComparison.OrdinalIgnoreCase)).Append(id).TakeLast(MaxRetired)];

    public static SoundboardLibrary Empty { get; } = new();

    /// <summary>~/.config/openxlr/soundboard.json.</summary>
    public static string DefaultPath => OpenXlrPaths.ConfigFile("soundboard.json");

    /// <summary>~/.local/share/openxlr/soundboard, where imported clips are copied.</summary>
    public static string ClipDirectory => Path.Combine(OpenXlrPaths.DataDir, "soundboard");

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>A clip id from a name, unique against <paramref name="existing"/>, the way channel ids are made.</summary>
    public static string NewId(string name, IEnumerable<string> existing) => MixerConfig.NewId(name, "clip", existing);

    /// <summary>An id of the shape <see cref="NewId"/> makes: lower-case letters, digits and hyphens, starting with a letter.</summary>
    public static bool IsValidId(string? id)
        => id is { Length: > 0 and <= MaxIdLength } && char.IsAsciiLetterLower(id[0])
            && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>1 to 60 characters with no control character, after trimming.</summary>
    public static bool IsValidName(string? name)
        => name is not null && name.Trim() is { Length: > 0 and <= MaxNameLength } trimmed && !trimmed.Any(char.IsControl);

    /// <summary>
    /// What clients that cannot send an empty name, such as the terminal
    /// mixer's prompts, type for the default page; never a page's own name.
    /// </summary>
    public const string DefaultPageWord = "-";

    /// <summary>
    /// A page name: empty for the default page, or up to 40 characters with
    /// no control character and no space at either end, other than
    /// <see cref="DefaultPageWord"/> alone.
    /// </summary>
    public static bool IsValidPage(string? page)
        => page is { Length: <= MaxPageLength } && page == page.Trim() && !page.Any(char.IsControl) && page != DefaultPageWord;

    /// <summary>
    /// The pages in the order clients draw them: the default page first while
    /// it holds a clip, then each named page where its first clip stands in
    /// the library. A page exists while a clip is on it.
    /// </summary>
    public static IReadOnlyList<string> PagesOf(IEnumerable<SoundboardClip> clips)
    {
        var pages = new List<string>();
        bool hasDefault = false;
        foreach (SoundboardClip clip in clips)
        {
            if (clip.Page.Length == 0) hasDefault = true;
            else if (!pages.Contains(clip.Page, StringComparer.Ordinal)) pages.Add(clip.Page);
        }
        if (hasDefault) pages.Insert(0, "");
        return pages;
    }

    /// <summary>
    /// A bare file name the daemon may open inside the clip directory: no
    /// separator, no <c>..</c>, no control character, not hidden, at most
    /// 255 bytes, ending in one of <see cref="Extensions"/>.
    /// </summary>
    public static bool IsSafeFileName(string? file)
    {
        if (string.IsNullOrEmpty(file) || file[0] == '.' || Encoding.UTF8.GetByteCount(file) > MaxFileNameBytes) return false;
        if (file.Contains('/') || file.Contains('\\') || file.Contains("..", StringComparison.Ordinal) || file.Any(char.IsControl)) return false;
        int dot = file.LastIndexOf('.');
        return dot > 0 && Extensions.Contains(file[(dot + 1)..], StringComparer.Ordinal);
    }

    /// <summary>Why a clip cannot be kept, or null when it can.</summary>
    public static string? Problem(SoundboardClip clip)
    {
        if (!IsValidId(clip.Id)) return "the id is not a clip id";
        if (!IsValidName(clip.Name)) return "the name must have 1 to 60 printable characters";
        if (!IsSafeFileName(clip.File)) return "the file must be a plain clip file name";
        if (!double.IsFinite(clip.Gain) || clip.Gain is < 0 or > 1) return "the gain must be from 0 to 1";
        if (!Modes.Contains(clip.Mode, StringComparer.Ordinal)) return "the mode must be overlap, restart, toggle or hold";
        if (!LayoutAppearance.IsValid(new LayoutAppearance(clip.Icon ?? "\0", clip.Colour))) return "the icon or colour is not valid";
        if (!double.IsFinite(clip.Seconds) || clip.Seconds is < 0 or > MaxSeconds + 1) return "the length is not valid";
        if (!IsValidPage(clip.Page)) return $"the page must have at most {MaxPageLength} printable characters, and cannot be {DefaultPageWord} alone";
        return null;
    }

    /// <summary>
    /// Read the library. A missing file is an empty, disabled library. An
    /// entry that breaks a rule is dropped and named in
    /// <paramref name="warning"/>; a file that cannot be read at all is
    /// copied next to itself with a ".corrupt" suffix, because the next save
    /// replaces it, and the library starts empty.
    /// </summary>
    public static SoundboardLibrary Load(string? path, out string? warning)
    {
        path ??= DefaultPath;
        warning = null;
        try
        {
            if (!System.IO.File.Exists(path)) return Empty;
            string text = RegularFile.ReadText(path, OpenXlrPaths.MaxPreferenceBytes);
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("the file does not hold an object");
            bool enabled = root.TryGetProperty("enabled", out JsonElement on) && on.ValueKind == JsonValueKind.True;
            var dropped = new List<string>();
            List<SoundboardClip> clips = ReadClips(root, dropped);
            var ids = new HashSet<string>(clips.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            var retired = new List<string>();
            if (root.TryGetProperty("retired", out JsonElement gone) && gone.ValueKind == JsonValueKind.Array)
                foreach (JsonElement entry in gone.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } id && IsValidId(id)
                        && !ids.Contains(id) && !retired.Contains(id, StringComparer.OrdinalIgnoreCase))
                        retired.Add(id);
            if (dropped.Count > 0) warning = $"{path}: dropped {string.Join(", ", dropped)}";
            return new SoundboardLibrary { Enabled = enabled, Clips = clips, Retired = [.. retired.TakeLast(MaxRetired)] };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warning = $"{path}: {ex.Message}";
            string copy = path + ".corrupt";
            try
            {
                RegularFile.Require(path);
                OpenXlrPaths.WriteAtomic(copy, System.IO.File.ReadAllBytes(path));
                warning += $"; copy kept as {copy}";
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                warning += $"; could not keep a copy as {copy}: {copyEx.Message}";
            }
            return Empty;
        }
    }

    /// <summary>
    /// The clips of a library object's <c>clips</c> list that keep every rule,
    /// in order. Each one that breaks a rule is named in
    /// <paramref name="dropped"/> with the reason, and the list stops taking
    /// clips at <see cref="MaxClips"/>.
    /// </summary>
    internal static List<SoundboardClip> ReadClips(JsonElement root, List<string> dropped)
    {
        var clips = new List<SoundboardClip>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.Ordinal);
        // Pages that differ only in case are one page, under the spelling its first clip has.
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("clips", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return clips;
        int index = 0;
        foreach (JsonElement entry in list.EnumerateArray())
        {
            index++;
            SoundboardClip? clip;
            try { clip = entry.Deserialize<SoundboardClip>(Json); }
            catch (JsonException) { clip = null; }
            // A file from before pages has none; spaces around a page are not part of it.
            if (clip is not null) clip = clip with { Page = clip.Page?.Trim() ?? "" };
            string label = clip is not null && IsValidId(clip.Id) ? clip.Id : $"#{index}";
            string? problem = clip is null ? "the entry is not a clip" : Problem(clip);
            if (problem is null && !ids.Add(clip!.Id)) problem = "the id is used twice";
            if (problem is null && !files.Add(clip!.File)) problem = "the file is used twice";
            if (problem is null && clips.Count >= MaxClips) problem = $"a library holds at most {MaxClips} clips";
            if (problem is not null) { dropped.Add($"{label} ({problem})"); continue; }
            string page = clip!.Page.Length == 0 ? "" : pages.TryAdd(clip.Page, clip.Page) ? clip.Page : pages[clip.Page];
            clips.Add(clip with { Name = clip.Name.Trim(), Page = page });
        }
        return clips;
    }

    /// <summary>Write the library atomically. Returns the reason when it could not be written, else null.</summary>
    public string? Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            OpenXlrPaths.WriteAtomicJson(path, this, Json, OpenXlrPaths.MaxPreferenceBytes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
