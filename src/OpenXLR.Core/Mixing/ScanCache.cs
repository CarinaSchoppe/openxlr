using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What the native host said about each bundle, kept between daemon runs.
/// Describing a module means creating every plugin in it, and one with two
/// hundred takes a quarter of a minute, which is too long to spend at every
/// start. A bundle is read again only when it changes, or when the helper
/// that reads it does.
///
/// One module's description can run to megabytes, and the daemon lives
/// under a firm heap limit, so the descriptions stay as bytes in a file
/// each; only a small index of stamps is ever held whole.
///
/// A description is also only what one helper had to say, so each entry
/// records which helper wrote it. When the helper changes it may describe
/// the same bundle differently, and without this an updated OpenXLR would
/// keep answering from what the old one learnt about every plugin already
/// installed, for as long as those bundles sat untouched.
/// </summary>
public sealed class ScanCache
{
    /// <summary>One bundle as it was when scanned, where its description is, and what wrote it.</summary>
    /// <param name="Target">
    /// Where a bundle that is itself a symbolic link pointed, so a link aimed
    /// at another file of the same length and timestamp is not mistaken for
    /// the one that was scanned. Null for anything that is not a link, which
    /// is also what entries written before this was recorded carry.
    /// </param>
    public sealed record Entry(long Modified, long Size, string File, string? Scanner = null, string? Target = null);

    private readonly string _directory;
    private readonly string _scanner;
    private readonly Dictionary<string, Entry> _index;
    private bool _dirty;

    public ScanCache(string directory, string? scanner = null)
    {
        _directory = directory;
        _scanner = string.IsNullOrEmpty(scanner) ? ScannerStamp : scanner;
        _index = Load(Path.Combine(directory, "index.json"));
    }

    /// <summary>
    /// The helper that does the scanning, as its file on disk: an OpenXLR
    /// that ships a new one asks it about every bundle again. Entries
    /// written before this was recorded carry no stamp and match nothing,
    /// so each of them costs one scan and is then kept as usual.
    /// </summary>
    public static string ScannerStamp => StampText(NativePluginHost.Executable);

    /// <summary>One file's stamp as a word, or "none" where there is no such file.</summary>
    internal static string StampText(string path) => Stamp(path) is { } stamp
        ? stamp.Size.ToString(CultureInfo.InvariantCulture) + "-" + stamp.Modified.ToString(CultureInfo.InvariantCulture)
        : "none";

    /// <summary>Where the daemon keeps it: under the user's cache directory.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(cacheHome))
                cacheHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cacheHome, "openxlr", "plugin-scans");
        }
    }

    /// <summary>The description of a bundle that has not changed since, read by this same helper, or null.</summary>
    public byte[]? Lookup(string bundle)
    {
        if (!_index.TryGetValue(bundle, out Entry? entry) || !string.Equals(entry.Scanner, _scanner, StringComparison.Ordinal)
            || Stamp(bundle) is not { } stamp
            || entry.Modified != stamp.Modified || entry.Size != stamp.Size
            || !string.Equals(entry.Target, stamp.Target, StringComparison.Ordinal))
            return null;
        try { return File.ReadAllBytes(Path.Combine(_directory, entry.File)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Store(string bundle, byte[] description)
    {
        if (Stamp(bundle) is not { } stamp) return;
        string file = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(bundle))) + ".json";
        try
        {
            OpenXlrPaths.EnsurePrivateDir(_directory);
            string temporary = Path.Combine(_directory, file + ".tmp");
            using (FileStream stream = OpenXlrPaths.CreatePrivate(temporary))
                stream.Write(description);
            File.Move(temporary, Path.Combine(_directory, file), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        _index[bundle] = new Entry(stamp.Modified, stamp.Size, file, _scanner, stamp.Target);
        _dirty = true;
    }

    /// <summary>Forget bundles that are gone, and write the index if anything changed.</summary>
    public void Save()
    {
        foreach ((string bundle, Entry entry) in _index.Where(e => !File.Exists(e.Key) && !Directory.Exists(e.Key)).ToList())
        {
            _index.Remove(bundle);
            try { File.Delete(Path.Combine(_directory, entry.File)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _dirty = true;
        }
        if (!_dirty) return;
        try
        {
            OpenXlrPaths.EnsurePrivateDir(_directory);
            OpenXlrPaths.WriteAtomic(Path.Combine(_directory, "index.json"), JsonSerializer.Serialize(_index, Options));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next run scans again */ }
    }

    /// <summary>
    /// A bundle's identity on disk: the files its links load, and where those
    /// links point. A directory folds every file's relative path, link target
    /// and target stamp into one hash, so an updated, retargeted, broken or
    /// repaired link makes a different bundle.
    ///
    /// A link with nothing behind it is stamped as broken rather than leaving
    /// the whole bundle unstampable. A bundle carries files the host never
    /// loads, a wrapper for another architecture among them, and one leftover
    /// link to a plugin that was removed must not cost a full rescan of the
    /// bundle at every start. Whether the files the host does need are usable
    /// is the scanner's answer, not the cache's: a scan that fails is never
    /// stored, and a source that disappears changes the stamp, so the entry
    /// that was already there stops being used.
    /// </summary>
    internal static (long Modified, long Size, string? Target)? Stamp(string bundle)
    {
        try
        {
            if (File.Exists(bundle))
            {
                FileIdentity one = FileStamp(bundle);
                return one.Broken ? null : (one.Modified, one.Size, one.Target);
            }
            if (!Directory.Exists(bundle)) return null;
            long total = 0;
            using var stamp = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] numbers = new byte[16];
            foreach (string file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                FileIdentity identity = FileStamp(file);
                // The newest file alone can hide an updated Windows source
                // whose timestamp is still older than the Linux wrapper. Only
                // a link adds its target, so a bundle of plain files keeps the
                // stamp it already has in the index and needs no rescan.
                stamp.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(bundle, file) + "\0"));
                if (identity.Target.Length > 0) stamp.AppendData(Encoding.UTF8.GetBytes(identity.Target + "\0"));
                BinaryPrimitives.WriteInt64LittleEndian(numbers, identity.Modified);
                BinaryPrimitives.WriteInt64LittleEndian(numbers.AsSpan(8), identity.Size);
                stamp.AppendData(numbers);
                total += identity.Size;
            }
            return (BinaryPrimitives.ReadInt64LittleEndian(stamp.GetHashAndReset()), total, DirectoryTarget(bundle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Where a bundle that is itself a link to another directory points. The
    /// managed folder links to Windows plugins installed elsewhere, and a
    /// link aimed at a different copy is a different bundle even when the two
    /// hold files of the same names, lengths and times. Null for a real
    /// directory.
    /// </summary>
    private static string? DirectoryTarget(string bundle)
    {
        var info = new DirectoryInfo(bundle);
        if (info.LinkTarget is null) return null;
        try
        {
            if (info.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo { Exists: true } resolved)
                return resolved.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a loop, or an unreadable path */ }
        return "missing:" + Path.GetFullPath(info.LinkTarget, info.Parent?.FullName ?? ".");
    }

    /// <summary>One file as the host would load it, and what it resolves to.</summary>
    /// <param name="Target">Empty for a plain file, else where the link ends up.</param>
    /// <param name="Broken">The file is a link with nothing behind it, or is gone.</param>
    private readonly record struct FileIdentity(long Modified, long Size, string Target, bool Broken);

    private static FileIdentity FileStamp(string path)
    {
        var info = new FileInfo(path);
        // Bridge bundles link to the original Windows module. FileInfo on
        // the link describes the link itself, which stays unchanged when the
        // plugin is updated, moved or deleted. Stamp the loaded file instead,
        // and keep the resolved path: two builds of one plugin can share a
        // length and a timestamp, and then only the path tells them apart.
        if (info.LinkTarget is null)
            return info.Exists ? new(info.LastWriteTimeUtc.Ticks, info.Length, "", false) : new(0, 0, "", true);
        try
        {
            if (info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } resolved)
                return new(resolved.LastWriteTimeUtc.Ticks, resolved.Length, resolved.FullName, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a loop, or an unreadable path */ }
        // Where a broken link points is part of the identity too, so aiming
        // it somewhere else, or putting the file back, reads as a change.
        return new(0, 0, "missing:" + Path.GetFullPath(info.LinkTarget, info.DirectoryName ?? "."), true);
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), Options)
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);   // a damaged cache is just a slow start
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
