using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// A refusal of a whole soundboard archive: its structure, its size or its
/// library file. A clip that cannot be taken is left out instead, and the
/// rest of the archive is imported.
/// </summary>
public sealed class SoundboardArchiveRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// The soundboard's library as one zip archive: <c>soundboard.json</c>, which
/// lists the clips as the library keeps them, and each clip's file under
/// <c>clips/</c>. Reading holds the archive to a few fixed limits before any
/// byte is unpacked: its size, its entry count, every entry's name, which
/// must be the library file or a clip file name the library would take, and
/// its type, which must be a plain file.
/// </summary>
public static class SoundboardArchive
{
    /// <summary>The library file's name in the archive.</summary>
    public const string LibraryEntry = "soundboard.json";

    /// <summary>The folder in the archive the clip files are in.</summary>
    public const string ClipFolder = "clips/";

    /// <summary>The largest archive, and the most its clips may unpack to.</summary>
    public const long MaxBytes = 512L << 20;

    /// <summary>The most entries an archive may hold: the library file, the clip folder and one file per clip.</summary>
    public const int MaxEntries = SoundboardLibrary.MaxClips + 2;

    /// <summary>The archive format this build writes and reads.</summary>
    public const int Format = 1;

    // The end of central directory record: its signature, its fixed size,
    // and the most a comment after it can add. A zip64 locator, which only
    // archives past 4 GiB or 65535 entries need, sits 20 bytes before it.
    private const uint EndSignature = 0x06054b50, Zip64LocatorSignature = 0x07064b50;
    private const int EndSize = 22, MaxComment = 65535, Zip64LocatorSize = 20;
    private const int RegularType = 0x8000, DirectoryType = 0x4000, TypeMask = 0xF000;
    // A regular file, readable and writable by its owner only, in the high half of the external attributes.
    private const int PrivateFileAttributes = unchecked((int)((uint)(RegularType | 0x180) << 16));

    private sealed record LibraryFile(int Format, IReadOnlyList<SoundboardClip> Clips);

    /// <summary>
    /// Write <paramref name="clips"/> and their files from
    /// <paramref name="directory"/> into <paramref name="output"/> as one
    /// archive. A clip whose file cannot be opened, or is not a regular file
    /// of at most 64 MiB, is left out and named in <paramref name="skipped"/>.
    /// Returns the clips the archive holds.
    /// </summary>
    public static IReadOnlyList<SoundboardClip> Write(Stream output, IReadOnlyList<SoundboardClip> clips, string directory, List<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(skipped);
        var written = new List<SoundboardClip>();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        long total = 0;
        var buffer = new byte[81920];
        foreach (SoundboardClip clip in clips)
        {
            FileStream input;
            try { input = Mixer.OpenClip(Path.Combine(directory, clip.File)); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{clip.Name}: {ex.Message}");
                continue;
            }
            using (input)
            {
                if (input.Length > SoundboardLibrary.MaxFileBytes)
                {
                    skipped.Add($"{clip.Name}: the file is larger than {SoundboardLibrary.MaxFileBytes >> 20} MiB.");
                    continue;
                }
                if (total + input.Length > MaxBytes)
                    throw new SoundboardArchiveRefusedException($"The clips add up to more than {MaxBytes >> 20} MiB, more than an archive holds.");
                ZipArchiveEntry entry = zip.CreateEntry(ClipFolder + clip.File, CompressionLevel.Fastest);
                entry.ExternalAttributes = PrivateFileAttributes;
                using Stream into = entry.Open();
                long copied = 0;
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    copied += read;
                    // A file that grew since it was opened is cut where the limits are.
                    if (copied > SoundboardLibrary.MaxFileBytes || total + copied > MaxBytes)
                        throw new SoundboardArchiveRefusedException($"{clip.Name} grew past the limits while it was exported.");
                    into.Write(buffer, 0, read);
                }
                total += copied;
            }
            written.Add(clip);
        }
        ZipArchiveEntry library = zip.CreateEntry(LibraryEntry, CompressionLevel.Optimal);
        library.ExternalAttributes = PrivateFileAttributes;
        using (Stream into = library.Open())
            JsonSerializer.Serialize(into, new LibraryFile(Format, written), SoundboardLibrary.Json);
        return written;
    }

    /// <summary>
    /// Read the end of the archive's central directory before anything else
    /// does, and refuse an archive that says it holds more entries than a
    /// library can, a directory larger than those entries need, or the zip64
    /// form, which no soundboard archive needs. The stream is left at its start.
    /// </summary>
    public static void CheckDirectory(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        if (length < EndSize) throw NotAnArchive();
        int tail = (int)Math.Min(length, EndSize + MaxComment + Zip64LocatorSize);
        var buffer = new byte[tail];
        stream.Position = length - tail;
        stream.ReadExactly(buffer);
        stream.Position = 0;
        for (int at = tail - EndSize; at >= 0; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at)) != EndSignature) continue;
            int disk = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 4));
            int here = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 8));
            int entries = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 10));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 12));
            bool zip64 = at >= Zip64LocatorSize
                && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at - Zip64LocatorSize)) == Zip64LocatorSignature;
            if (disk != 0 || here != entries || zip64 || entries == ushort.MaxValue || size == uint.MaxValue)
                throw new SoundboardArchiveRefusedException("The archive is in a form a soundboard archive never takes: split over several files, or zip64.");
            if (entries > MaxEntries)
                throw new SoundboardArchiveRefusedException($"The archive holds {entries} entries; a soundboard archive holds at most {MaxEntries}.");
            // A directory record is 46 bytes and a name of up to 255; a few
            // bytes of extra fields each are allowed for.
            if (size > (long)MaxEntries * (46 + 255 + 1024))
                throw new SoundboardArchiveRefusedException("The archive's directory is larger than its entries need.");
            return;
        }
        throw NotAnArchive();
    }

    private static SoundboardArchiveRefusedException NotAnArchive() => new("The file is not a zip archive.");

    /// <summary>
    /// What an archive holds: the clips its library file lists that keep the
    /// library's rules, in order, and its clip files by name. Every entry is
    /// checked first, and one that is not the library file, the clip folder
    /// or a clip file the library would take, or that is a link or a special
    /// file, refuses the whole archive. A clip the library file lists that
    /// breaks a rule is named in <paramref name="skipped"/>.
    /// </summary>
    public static (List<SoundboardClip> Clips, Dictionary<string, ZipArchiveEntry> Files) Read(ZipArchive zip, List<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(skipped);
        if (zip.Entries.Count > MaxEntries)
            throw new SoundboardArchiveRefusedException($"The archive holds {zip.Entries.Count} entries; a soundboard archive holds at most {MaxEntries}.");
        ZipArchiveEntry? library = null;
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long declared = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName;
            int type = (int)((uint)entry.ExternalAttributes >> 16) & TypeMask;
            if (name == ClipFolder && entry.Length == 0)
            {
                if (type is not (0 or DirectoryType)) throw Unexpected(name);
                continue;
            }
            if (type is not (0 or RegularType)) throw new SoundboardArchiveRefusedException($"The archive holds a link or a special file: {Shown(name)}.");
            if (name == LibraryEntry)
            {
                if (library is not null) throw Unexpected(name);
                library = entry;
                continue;
            }
            if (!name.StartsWith(ClipFolder, StringComparison.Ordinal) || !SoundboardLibrary.IsSafeFileName(name[ClipFolder.Length..])
                || !files.TryAdd(name[ClipFolder.Length..], entry))
                throw Unexpected(name);
            declared += entry.Length;
        }
        if (library is null) throw new SoundboardArchiveRefusedException($"The archive has no {LibraryEntry}, so it is not a soundboard archive.");
        if (declared > MaxBytes) throw new SoundboardArchiveRefusedException($"The archive unpacks to more than {MaxBytes >> 20} MiB.");

        string text;
        try
        {
            using var memory = new MemoryStream();
            CopyCapped(library, memory, OpenXlrPaths.MaxPreferenceBytes);
            text = System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }
        catch (InvalidDataException ex) { throw new SoundboardArchiveRefusedException($"The archive's {LibraryEntry} is damaged: {ex.Message}"); }
        catch (InvalidOperationException ex) when (ex is not SoundboardArchiveRefusedException)
        {
            throw new SoundboardArchiveRefusedException($"The archive's {LibraryEntry} cannot be read: {ex.Message}");
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("clips", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                throw new SoundboardArchiveRefusedException($"The archive's {LibraryEntry} lists no clips.");
            if (root.TryGetProperty("format", out JsonElement format)
                && (format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out int version) || version > Format))
                throw new SoundboardArchiveRefusedException("The archive was made by a newer OpenXLR. Update OpenXLR to import it.");
            var dropped = new List<string>();
            List<SoundboardClip> clips = SoundboardLibrary.ReadClips(root, dropped);
            skipped.AddRange(dropped.Select(d => $"{d}: the entry breaks the library's rules"));
            return (clips, files);
        }
        catch (JsonException ex) { throw new SoundboardArchiveRefusedException($"The archive's {LibraryEntry} is not valid JSON: {ex.Message}"); }
    }

    /// <summary>
    /// Unpack a clip file into a new private file at <paramref name="target"/>.
    /// A file over 64 MiB is refused as importClip refuses it. An entry whose
    /// data is not the size and checksum it declares, or that declares more
    /// than <paramref name="budget"/> bytes, refuses the whole archive.
    /// Returns the bytes written.
    /// </summary>
    public static long Extract(ZipArchiveEntry entry, string target, long budget)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Length > SoundboardLibrary.MaxFileBytes)
            throw new InvalidOperationException($"The file is {entry.Length / 1048576.0:0.0} MiB; the limit is {SoundboardLibrary.MaxFileBytes >> 20} MiB.");
        if (entry.Length > budget) throw new SoundboardArchiveRefusedException($"The archive unpacks to more than {MaxBytes >> 20} MiB.");
        using FileStream output = OpenXlrPaths.CreatePrivate(target);
        long copied = CopyCapped(entry, output, entry.Length);
        if (copied == 0) throw new InvalidOperationException("The file is empty.");
        return copied;
    }

    /// <summary>
    /// Copy an entry, never more than it declares or than
    /// <paramref name="limit"/>, and refuse the archive unless what came out
    /// is the size and the checksum the entry declares. .NET's reader stops
    /// at the declared size, so an entry that holds more than it declares
    /// cannot be seen by reading on; its checksum, taken over all its data,
    /// does not match the part read, and that refuses it.
    /// </summary>
    private static long CopyCapped(ZipArchiveEntry entry, Stream output, long limit)
    {
        if (entry.Length > limit) throw new SoundboardArchiveRefusedException($"{Shown(entry.FullName)} is larger than a soundboard archive allows.");
        using Stream input = entry.Open();
        var buffer = new byte[81920];
        long copied = 0;
        uint crc = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            copied += read;
            if (copied > entry.Length || copied > limit)
                throw new SoundboardArchiveRefusedException($"The archive is damaged: {Shown(entry.FullName)} unpacks to more than it declares.");
            crc = Crc32(crc, buffer.AsSpan(0, read));
            output.Write(buffer, 0, read);
        }
        if (copied != entry.Length || crc != entry.Crc32)
            throw new SoundboardArchiveRefusedException($"The archive is damaged: {Shown(entry.FullName)} does not match the size and checksum it declares.");
        return copied;
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    })];

    /// <summary>The zip format's CRC-32 of <paramref name="data"/>, carried on from <paramref name="crc"/>.</summary>
    internal static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static SoundboardArchiveRefusedException Unexpected(string name)
        => new($"The archive holds an entry a soundboard archive never has: {Shown(name)}.");

    /// <summary>An entry name fit for a sentence: printable, and short.</summary>
    private static string Shown(string name)
    {
        string clean = new([.. name.Where(c => !char.IsControl(c))]);
        return clean.Length <= 60 ? clean : clean[..60] + "...";
    }
}
