using System.IO.Compression;

namespace OpenXLR.Core.Mixing;

/// <summary>What an export wrote: how many clips the archive holds, and each clip left out with the reason.</summary>
public sealed record SoundboardExport(int Clips, IReadOnlyList<string> Skipped);

/// <summary>
/// What an import did: the ids of the clips it added, in library order, each
/// clip left out with the reason, the clips that took a new id because theirs
/// was in use here (the archive's id to the new one), and whether the
/// library was replaced rather than added to.
/// </summary>
public sealed record SoundboardImport(IReadOnlyList<string> Added, IReadOnlyList<string> Skipped,
    IReadOnlyDictionary<string, string> Renamed, bool Replaced);

/// <summary>An import that changed nothing, with each clip that could not be taken.</summary>
public sealed class SoundboardImportRefusedException(string message, IReadOnlyList<string> skipped) : InvalidOperationException(message)
{
    public IReadOnlyList<string> Skipped { get; } = skipped;
}

public sealed partial class Mixer
{
    /// <summary>What an export or an import is refused with while another one runs.</summary>
    public const string SoundboardTransferBusy = "A soundboard library export or import is already running. Try again when it ends.";

    // One export or import at a time across the daemon: each can read or
    // write up to an archive's worth of data.
    private readonly SemaphoreSlim _boardTransfer = new(1, 1);

    /// <summary>An absolute path a client may name for an archive, or the sentence that refuses it.</summary>
    private static void RequireArchivePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Give the archive as an absolute path.");
    }

    /// <summary>
    /// Write the library and every clip file into a zip archive at
    /// <paramref name="path"/>, which must end in <c>.zip</c>. The archive is
    /// built whole first, as a private file in the clip directory, so
    /// nothing at <paramref name="path"/> is touched until it is complete.
    /// It then takes the path by a rename, which replaces a file or a link
    /// there whole or not at all: straight from the clip directory when both
    /// are on one file system, else through a private copy beside the path.
    /// A folder that lets only the chosen file be written, as a desktop
    /// portal hands to a sandbox, takes the archive in place instead: that
    /// write is not atomic, and it refuses a link and anything but a regular
    /// file. A clip whose file cannot be read is left out and named.
    /// </summary>
    public SoundboardExport ExportSoundboard(string path)
    {
        RequireArchivePath(path);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The archive's name must end in .zip.");
        if (!_boardTransfer.Wait(0)) throw new InvalidOperationException(SoundboardTransferBusy);
        string? built = null;
        try
        {
            SoundboardLibrary board;
            string directory;
            lock (_gate)
            {
                board = _board;
                directory = _clipDirectory;
            }
            if (board.Clips.Count == 0) throw new InvalidOperationException("The soundboard has no clips to export.");
            string parent = Path.GetDirectoryName(path) ?? "";
            if (!Directory.Exists(parent)) throw new InvalidOperationException("The folder to save the archive in does not exist.");
            // A link in the archive's place is replaced, not followed; a
            // folder, a pipe or a device is never written to.
            if (RegularFile.Inspect(path, noFollow: true) is { } existing && (existing.Mode & 0xf000) is not (0x8000 or 0xa000))
                throw new InvalidOperationException("Something other than a file is at that path.");

            var skipped = new List<string>();
            OpenXlrPaths.EnsurePrivateDir(directory);
            built = Path.Combine(directory, ".export-" + Guid.NewGuid().ToString("N") + ".tmp");
            IReadOnlyList<SoundboardClip> written;
            using (FileStream output = OpenXlrPaths.CreatePrivate(built))
                written = SoundboardArchive.Write(output, board.Clips, directory, skipped);
            if (written.Count == 0)
                throw new InvalidOperationException("No clip file could be read, so nothing was exported. " + string.Join(" ", skipped));
            Publish(built, path, parent);
            return new SoundboardExport(written.Count, skipped);
        }
        finally
        {
            if (built is not null) TryDelete(built);
            _boardTransfer.Release();
        }
    }

    /// <summary>
    /// Put the finished archive <paramref name="built"/> at
    /// <paramref name="path"/>. Nothing but a copy of ours is ever deleted:
    /// a failure leaves what was at the path as it was, except after the
    /// in-place write has begun.
    /// </summary>
    private static void Publish(string built, string path, string parent)
    {
        if (SameFileSystem(built, parent))
        {
            // A rename that is refused leaves both names as they were.
            try { File.Move(built, path, overwrite: true); }
            catch (UnauthorizedAccessException) { WriteInPlace(built, path); }
            return;
        }
        string staging = Path.Combine(parent, ".openxlr-" + Guid.NewGuid().ToString("N") + ".tmp");
        FileStream copy;
        try { copy = OpenXlrPaths.CreatePrivate(staging); }
        catch (UnauthorizedAccessException)
        {
            WriteInPlace(built, path);
            return;
        }
        bool moved = false;
        try
        {
            using (copy)
            using (FileStream source = File.OpenRead(built))
                source.CopyTo(copy);
            File.Move(staging, path, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved) TryDelete(staging);
        }
    }

    /// <summary>Whether a file and a folder are on one file system, so a rename moves the one into the other.</summary>
    private static bool SameFileSystem(string file, string folder)
    {
        try
        {
            return RegularFile.Inspect(file) is { } a && RegularFile.Inspect(folder) is { } b
                && a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;
        }
        catch (IOException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenForWriting([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    // Linux's values, the same on x86-64 and AArch64.
    private const int WriteOnly = 1, Create = 0x40, NoFollow = 0x20000, NonBlockingOpen = 0x800, CloseOnExecOpen = 0x80000;
    private const int LinkLoop = 40;

    /// <summary>
    /// Copy the archive over the chosen file in place, for a folder that
    /// lets nothing else be created. The open never follows a link, and the
    /// handle must be a regular file before a byte of it is cut. Not atomic:
    /// a failure part way leaves the file part written.
    /// </summary>
    private static void WriteInPlace(string built, string path)
    {
        int fd = OpenForWriting(path, WriteOnly | Create | NoFollow | NonBlockingOpen | CloseOnExecOpen, 0x180);
        if (fd < 0)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(error == LinkLoop ? "The archive's path is a link; choose the file itself."
                : $"The archive could not be written: {new System.ComponentModel.Win32Exception(error).Message}.");
        }
        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
        using var output = new FileStream(handle, FileAccess.Write);
        if (RegularFile.Inspect($"/proc/self/fd/{fd}") is not { } status || (status.Mode & 0xf000) != 0x8000)
            throw new InvalidOperationException("Something other than a file is at that path.");
        output.SetLength(0);
        using FileStream source = File.OpenRead(built);
        source.CopyTo(output);
    }

    /// <summary>
    /// Import a soundboard archive. With <paramref name="merge"/> its clips
    /// are added after the ones here, an id already in use, or once given to
    /// a deleted clip, made unique as an import makes it; without, its clips
    /// replace the library, keeping their ids, and the clips here are deleted
    /// with their files. Every clip file is unpacked into a private copy and
    /// checked by the helper as importClip checks a file. A merge leaves out
    /// a clip that fails and imports the rest; a replace changes nothing
    /// unless every clip can be imported. Nothing changes when no clip can be
    /// imported, and the refusal names each clip left out.
    /// </summary>
    public SoundboardImport ImportSoundboard(string path, bool merge)
    {
        RequireArchivePath(path);
        if (!_pw.PlayerHostInstalled) throw new InvalidOperationException(SoundboardNeedsHost);
        if (!_boardTransfer.Wait(0)) throw new InvalidOperationException(SoundboardTransferBusy);
        var staged = new List<(SoundboardClip Clip, string Staging, ClipProbe Probe)>();
        var skipped = new List<string>();
        try
        {
            string directory;
            int room;
            lock (_gate)
            {
                directory = _clipDirectory;
                room = merge ? SoundboardLibrary.MaxClips - _board.Clips.Count : SoundboardLibrary.MaxClips;
            }
            if (room <= 0) throw new InvalidOperationException($"The soundboard holds {SoundboardLibrary.MaxClips} clips already. Delete some first, or replace the library.");
            using (FileStream file = OpenClip(path))
            {
                if (file.Length > SoundboardArchive.MaxBytes)
                    throw new InvalidOperationException($"The file is {file.Length / 1048576.0:0.0} MiB; an archive is at most {SoundboardArchive.MaxBytes >> 20} MiB.");
                SoundboardArchive.CheckDirectory(file);
                ZipArchive zip;
                try { zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true); }
                catch (InvalidDataException) { throw new InvalidOperationException("The file is not a zip archive OpenXLR can read."); }
                using (zip)
                {
                    (List<SoundboardClip> clips, Dictionary<string, ZipArchiveEntry> files) = SoundboardArchive.Read(zip, skipped);
                    if (clips.Count == 0) throw new SoundboardImportRefusedException(Nothing("The archive holds no clip to import.", skipped), skipped);
                    OpenXlrPaths.EnsurePrivateDir(directory);
                    long budget = SoundboardArchive.MaxBytes;
                    foreach (SoundboardClip clip in clips)
                    {
                        if (staged.Count >= room)
                        {
                            skipped.Add($"{clip.Name}: the soundboard holds at most {SoundboardLibrary.MaxClips} clips");
                            continue;
                        }
                        if (!files.TryGetValue(clip.File, out ZipArchiveEntry? entry))
                        {
                            skipped.Add($"{clip.Name}: its file is not in the archive");
                            continue;
                        }
                        string staging = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            budget -= SoundboardArchive.Extract(entry, staging, budget);
                            // The unpacked copy is what is checked and what the player reads.
                            ClipProbe probe = _pw.ProbeClip(staging);
                            if (probe.Seconds > SoundboardLibrary.MaxSeconds)
                                throw new InvalidOperationException($"The clip is {probe.Seconds:0.0} s long; the limit is {SoundboardLibrary.MaxSeconds} s.");
                            staged.Add((clip, staging, probe));
                        }
                        catch (SoundboardArchiveRefusedException)
                        {
                            TryDelete(staging);
                            throw;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
                        {
                            TryDelete(staging);
                            skipped.Add($"{clip.Name}: {Sentence(ex.Message)}");
                        }
                    }
                }
            }
            if (staged.Count == 0) throw new SoundboardImportRefusedException(Nothing("No clip in the archive could be imported.", skipped), skipped);
            // A replace that would leave clips out deletes the library for
            // less than the archive holds, as when this build cannot decode
            // a format the other one could: nothing is replaced.
            if (!merge && skipped.Count > 0)
                throw new SoundboardImportRefusedException(Nothing(skipped.Count == 1
                    ? "Nothing was replaced, because 1 clip in the archive cannot be imported here."
                    : $"Nothing was replaced, because {skipped.Count} clips in the archive cannot be imported here.", skipped), skipped);
            lock (_gate) return CommitImportLocked(directory, merge, staged, skipped);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"The archive is damaged: {ex.Message}");
        }
        finally
        {
            // A copy the library took was renamed away; the rest go.
            foreach ((_, string staging, _) in staged) TryDelete(staging);
            _boardTransfer.Release();
        }
    }

    private static string Nothing(string sentence, List<string> skipped)
        => skipped.Count == 0 ? sentence : $"{sentence} {string.Join("; ", skipped)}.";

    private static string Sentence(string message)
        => message.Length == 0 ? message : char.ToLowerInvariant(message[0]) + message[1..].TrimEnd('.');

    /// <summary>
    /// Put the checked copies in the library and save it, or leave both as
    /// they were. A copy never takes the file name of a clip in the library
    /// now, so a failed save is undone by deleting the copies.
    /// </summary>
    private SoundboardImport CommitImportLocked(string directory, bool merge,
        List<(SoundboardClip Clip, string Staging, ClipProbe Probe)> staged, List<string> skipped)
    {
        if (directory != _clipDirectory) throw new InvalidOperationException("The clip directory changed; import the archive again.");
        SoundboardLibrary before = _board;
        List<SoundboardClip> kept = merge ? [.. before.Clips] : [];
        var taken = new HashSet<string>(merge ? before.Clips.Select(c => c.Id).Concat(before.Retired) : [], StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(before.Clips.Select(c => c.File), StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new List<SoundboardClip>();
        var moved = new List<string>();
        try
        {
            foreach ((SoundboardClip clip, string staging, ClipProbe probe) in staged)
            {
                if (kept.Count + added.Count >= SoundboardLibrary.MaxClips)
                {
                    skipped.Add($"{clip.Name}: the soundboard holds at most {SoundboardLibrary.MaxClips} clips");
                    continue;
                }
                string id = taken.Contains(clip.Id) ? SoundboardLibrary.NewId(clip.Id, taken) : clip.Id;
                taken.Add(id);
                if (id != clip.Id) renamed[clip.Id] = id;
                string file = $"{id}.{probe.Extension}";
                for (int n = 2; names.Contains(file); n++) file = $"{id}-{n}.{probe.Extension}";
                names.Add(file);
                // A page here under the same letters in another case keeps its spelling.
                string page = merge ? SamePageLocked(clip.Page) : clip.Page;
                SoundboardClip entry = clip with { Id = id, File = file, Seconds = Math.Round(probe.Seconds, 3), Page = page };
                string target = Path.Combine(directory, file);
                File.Move(staging, target, overwrite: true);
                moved.Add(target);
                added.Add(entry);
            }
            IReadOnlyList<string> retired = before.Retired;
            if (!merge)
            {
                var ids = new HashSet<string>(added.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
                // The archive's ids are the point of a replace: a key bound to
                // one plays its clip again here, even when this library once
                // deleted a clip under it.
                retired = [.. retired.Where(r => !ids.Contains(r))];
                foreach (SoundboardClip gone in before.Clips.Where(c => !ids.Contains(c.Id))) retired = SoundboardLibrary.Retire(retired, gone.Id);
            }
            SaveBoardLocked(_board with { Clips = [.. kept, .. added], Retired = retired });
        }
        catch
        {
            foreach (string target in moved) TryDelete(target);
            throw;
        }

        if (merge)
        {
            foreach (SoundboardClip clip in added)
            {
                _crashedClips.Remove(clip.Id);
                if (_player is not { } player) continue;
                try { player.Add(clip.Id, Path.Combine(directory, clip.File), clip.Gain, clip.Loop); }
                catch (InvalidOperationException ex)
                {
                    // The next player loads every clip, these included.
                    _pw.Note($"The soundboard player did not take {clip.Id}, so it starts again: {ex.Message}");
                    StopPlayerLocked();
                    StartSoundboardIfWantedLocked();
                    break;
                }
            }
        }
        else
        {
            foreach (SoundboardClip old in before.Clips)
            {
                ForgetHolds(old.Id);
                TryDelete(Path.Combine(directory, old.File));
            }
            _crashedClips.Clear();
            // The player is started again on the new library, which it loads whole.
            if (_player is not null)
            {
                StopPlayerLocked();
                StartSoundboardIfWantedLocked();
            }
        }
        return new SoundboardImport([.. added.Select(c => c.Id)], skipped, renamed, !merge);
    }
}
