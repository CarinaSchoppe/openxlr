using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace OpenXLR.UI;

/// <summary>Only the exact startup paths this application writes can be removed here.</summary>
internal static class StartupReceipt
{
    private static readonly object Gate = new();
    internal static string Path => OpenXlrPaths.ConfigFile("installation.json");
    // A trailing separator in XDG_CONFIG_HOME would otherwise reach the
    // receipt prefix, which the receipt format rejects.
    private static string Root => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(OpenXlrPaths.ConfigHome));
    private static string Backup(string hash) => System.IO.Path.Combine(OpenXlrPaths.ConfigDir, "installation-backups", hash);
    internal static string[] StartupPaths => [System.IO.Path.Combine(Root, "autostart", "openxlr.desktop"),
        System.IO.Path.Combine(Root, "systemd", "user", "openxlr-daemon.service"),
        System.IO.Path.Combine(Root, "systemd", "user", "default.target.wants", "openxlr-daemon.service")];
    internal static bool Allowed(string path) => StartupPaths.Contains(path, StringComparer.Ordinal);

    private static bool OwnedBackup(string path) => System.IO.Path.GetFileName(path) is { Length: 64 } name &&
        name.All(char.IsAsciiHexDigit) && path == Backup(name);

    internal static InstallationReceipt Load()
    {
        if (InstallationReceipt.HasLinkedParent(Path, Root)) throw new IOException("The installation receipt directory is a symbolic link.");
        if (new FileInfo(Path).LinkTarget is not null) throw new IOException("The installation receipt is a symbolic link.");
        var receipt = File.Exists(Path) ? InstallationReceipt.Read(Path) : new InstallationReceipt
        { Package = "openxlr-startup", Manager = "user", Version = BuildVersion(), Prefix = Root, Commit = BuildCommit() };
        if (receipt.Manager != "user" || receipt.Prefix != Root || receipt.Package != "openxlr-startup" ||
            receipt.Files.Any(f => !(Allowed(f.Path) || f.Path == Path && f.Kind == "receipt" || OwnedBackup(f.Path) && f.Kind == "file")))
            throw new IOException("The startup receipt owns an unexpected path.");
        return receipt;
    }

    private static string BuildVersion() => typeof(StartupReceipt).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private static string? BuildCommit()
    {
        string? value = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(StartupReceipt).Assembly)?.InformationalVersion.Split('+').LastOrDefault();
        return value is { Length: 40 } && value.All(char.IsAsciiHexDigit) ? value : null;
    }

    private static void Save(InstallationReceipt receipt)
    {
        if (!receipt.Files.Any(f => f.Path == Path))
            receipt = receipt with { Files = [.. receipt.Files, new() { Path = Path, Kind = "receipt", Mode = 384 }] };
        receipt.Validate();
        OpenXlrPaths.WriteAtomicJson(Path, receipt, InstallationReceipt.Json);
    }

    internal static void Write(string path, string text)
    {
        lock (Gate)
        {
            using var lease = Lock();
            path = System.IO.Path.GetFullPath(path);
            if (!Allowed(path) || path == StartupPaths[2] || InstallationReceipt.HasLinkedParent(path, Root) || new FileInfo(path).LinkTarget is not null || Directory.Exists(path))
                throw new IOException("Refusing an unrecognized, linked or directory startup path.");
            InstallationReceipt receipt = AdoptLegacy(Load(), path, null);
            InstalledFile? old = receipt.Files.FirstOrDefault(f => f.Path == path);
            // Do not extend an installation whose original can no longer be
            // restored. Check before updating either the receipt or destination.
            if (old is { Removed: false, ReplacedSha256: not null }) _ = ReadBackup(old);
            string? replaced = old is { Removed: false } ? old.ReplacedSha256 : null;
            int? replacedMode = old is { Removed: false } ? old.ReplacedMode : null;
            byte[]? originalBytes = null;
            if (File.Exists(path) && (old is null || old.Removed || InstallationReceipt.Problem(old, Root) is not null))
            {
                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024) throw new IOException("Startup file exceeds 1 MiB.");
                originalBytes = RegularFile.ReadBytes(path, 1024 * 1024);
                replaced = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
                replacedMode = OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path);
                if (replacedMode > 511) throw new IOException("Special startup file permissions are not supported.");
                if (InstallationReceipt.HasLinkedParent(Backup(replaced), Root) || new FileInfo(Backup(replaced)).LinkTarget is not null)
                    throw new IOException("The backup path is a symbolic link.");
                if (File.Exists(Backup(replaced)) && InstallationReceipt.Digest(Backup(replaced)) != replaced)
                    throw new IOException("The original startup file backup was modified.");
                receipt = receipt with
                {
                    Files = [.. receipt.Files.Where(f => f.Path != Backup(replaced)),
                new() { Path = Backup(replaced), Sha256 = replaced, Mode = 384 }]
                };
            }
            var entry = new InstalledFile { Path = path, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), ReplacedSha256 = replaced, ReplacedMode = replacedMode };
            // Record the intended write before touching its destination. A failed or
            // interrupted write is then visible to doctor and never owns other paths.
            var next = receipt with { Version = BuildVersion(), Commit = BuildCommit(), Files = [.. receipt.Files.Where(f => f.Path != path), entry] };
            if (originalBytes is not null) OpenXlrPaths.WriteAtomic(Backup(replaced!), originalBytes);
            Save(next);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            OpenXlrPaths.WriteAtomic(path, text, privatePermissions: false);
            if (!OperatingSystem.IsWindows())
            {
                int mode = (int)File.GetUnixFileMode(path) & 511;
                Save(next with { Files = [.. next.Files.Select(f => f.Path == path ? f with { Mode = mode } : f)] });
            }
        }
    }

    internal static void EnableLink(string path, string target)
    {
        lock (Gate)
        {
            using var lease = Lock();
            path = System.IO.Path.GetFullPath(path);
            if (LinkProblem(path, target) is { } problem) throw new IOException(problem);
            var info = new FileInfo(path);
            InstallationReceipt receipt = Load();
            var entry = new InstalledFile { Path = path, Kind = "link", LinkTarget = info.LinkTarget ?? target };
            Save(receipt with { Files = [.. receipt.Files.Where(f => f.Path != path), entry] });
            if (info.LinkTarget is not null) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.CreateSymbolicLink(path, target);
        }
    }

    /// <summary>Whether <see cref="EnableLink"/> would accept the path, checked before anything else is written.</summary>
    internal static bool CanEnableLink(string path, string target) => LinkProblem(System.IO.Path.GetFullPath(path), target) is null;

    private static string? LinkProblem(string path, string target)
    {
        if (path != StartupPaths[2] || InstallationReceipt.HasLinkedParent(path, Root)) return "Unrecognized or linked startup directory.";
        var info = new FileInfo(path);
        if (Directory.Exists(path) && info.LinkTarget is null || info.Exists && info.LinkTarget is null || info.LinkTarget is not null && !SameTarget(info, target))
            return "Refusing to replace a foreign startup link.";
        return null;
    }

    /// <summary>True when a symbolic link at the path names the target, whether or not the target exists.</summary>
    internal static bool LinksTo(string path, string target)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is not null && SameTarget(info, target);
    }

    internal static void Remove(string path, string? packagedUnit = null)
    {
        lock (Gate)
        {
            path = System.IO.Path.GetFullPath(path);
            var info = new FileInfo(path);
            using var lease = Lock();
            InstallationReceipt receipt = AdoptLegacy(Load(), path, packagedUnit);
            if (RemovalProblem(receipt, path) is { } problem) throw new IOException(problem);
            InstalledFile? file = receipt.Files.FirstOrDefault(f => f.Path == path);
            if (file is null || file.Removed) return;
            if (!info.Exists && info.LinkTarget is null)
            {
                Save(receipt with { Files = [.. receipt.Files.Select(f => f.Path == path ? f with { Removed = true } : f)] });
                return;
            }
            if (file.ReplacedSha256 is not null)
            {
                byte[] originalBytes = ReadBackup(file);
                OpenXlrPaths.WriteAtomic(path, originalBytes, privatePermissions: false,
                    fileMode: (UnixFileMode)(file.ReplacedMode ?? 384));
            }
            else File.Delete(path);
            Save(receipt with { Files = [.. receipt.Files.Select(f => f.Path == path ? f with { Removed = true } : f)] });
        }
    }

    // Migration proves identity from generated bytes or the intended unit
    // target. A familiar filename alone never grants ownership.
    private static InstallationReceipt AdoptLegacy(InstallationReceipt receipt, string path, string? packagedUnit)
    {
        if (receipt.Files.Any(f => f.Path == path) || LegacyEntry(path, packagedUnit) is not { } entry) return receipt;
        var next = receipt with { Files = [.. receipt.Files, entry] };
        Save(next);
        return next;
    }

    /// <summary>
    /// Whether an unrecorded startup path would be adopted: a file equal to a
    /// template some release of this window wrote for the program it names, or
    /// an enable link to the user unit path or the packaged unit. The link is
    /// ours by name and target alone; systemctl enable leaves it behind when
    /// the unit it pointed at is gone. Read-only, for doctor.
    /// </summary>
    internal static bool Recognized(string path, string? packagedUnit) => LegacyEntry(path, packagedUnit) is not null;

    private static InstalledFile? LegacyEntry(string path, string? packagedUnit)
    {
        if (!Allowed(path) || InstallationReceipt.HasLinkedParent(path, Root)) return null;
        var info = new FileInfo(path);
        if (path == StartupPaths[2])
            return info.LinkTarget is not null && (SameTarget(info, StartupPaths[1]) || packagedUnit is not null && SameTarget(info, packagedUnit))
                ? new InstalledFile { Path = path, Kind = "link", LinkTarget = info.LinkTarget }
                : null;
        if (!info.Exists || info.LinkTarget is not null || Directory.Exists(path)) return null;
        byte[] bytes = RegularFile.ReadBytes(path, 1024 * 1024);
        string text = Encoding.UTF8.GetString(bytes);
        if (!bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text)) || !StartupIntegration.IsGeneratedStartupFile(path, text)) return null;
        return new() { Path = path, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Mode = OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path) };
    }

    internal static InstallationReceipt PrepareRemoval(string? packagedUnit = null, bool includeWindow = true)
    {
        lock (Gate)
        {
            using var lease = Lock();
            InstallationReceipt receipt = Load();
            foreach (string path in includeWindow ? StartupPaths : StartupPaths.Skip(1)) receipt = AdoptLegacy(receipt, path, packagedUnit ?? StartupIntegration.PackagedUnit);
            return receipt;
        }
    }

    internal static string Recover()
    {
        lock (Gate)
        {
            using var lease = Lock();
            if (!File.Exists(Path)) throw new IOException("No startup receipt needs recovery.");
            if (new FileInfo(Path).LinkTarget is not null || InstallationReceipt.HasLinkedParent(Path, Root))
                throw new IOException("Move the linked receipt aside manually before recovery.");
            RegularFile.Require(Path);
            bool invalid = false;
            try { _ = Load(); }
            catch (IOException) { invalid = true; }
            if (!invalid) throw new IOException("The startup receipt is valid; it was left unchanged.");
            string saved = Path + ".before-recovery-" + Guid.NewGuid().ToString("N");
            File.Move(Path, saved);
            return saved;
        }
    }

    internal static string? RemovalProblem(InstallationReceipt receipt, string path)
    {
        if (!Allowed(path) || InstallationReceipt.HasLinkedParent(path, Root)) return "Unrecognized or linked startup path: " + path;
        var info = new FileInfo(path);
        if (Directory.Exists(path) && info.LinkTarget is null) return "Refusing to remove a startup directory: " + path;
        if (!info.Exists && info.LinkTarget is null) return null;
        InstalledFile? file = receipt.Files.FirstOrDefault(f => f.Path == path);
        if (file is { Removed: true, ReplacedSha256: { } original } && info.LinkTarget is null && InstallationReceipt.Digest(path) == original) return null;
        if (file is not { Removed: false } || InstallationReceipt.Problem(file, Root) is not null)
            return "Refusing to remove an unowned or modified startup path: " + path;
        if (file.ReplacedSha256 is not null) _ = ReadBackup(file);
        return null;
    }

    private static byte[] ReadBackup(InstalledFile file)
    {
        string backup = Backup(file.ReplacedSha256!);
        if (InstallationReceipt.HasLinkedParent(backup, Root) || new FileInfo(backup).LinkTarget is not null)
            throw new IOException("The original startup file backup path is a symbolic link.");
        byte[] bytes = RegularFile.ReadBytes(backup, 1024 * 1024);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.ReplacedSha256)
            throw new IOException("The original startup file backup is missing or modified.");
        return bytes;
    }

    private static bool SameTarget(FileInfo link, string target)
    {
        string absolute = System.IO.Path.GetFullPath(link.LinkTarget!, link.DirectoryName!);
        if (absolute == System.IO.Path.GetFullPath(target)) return true;
        var expected = new FileInfo(target);
        FileSystemInfo? resolved = link.ResolveLinkTarget(returnFinalTarget: true);
        FileSystemInfo destination = expected.LinkTarget is null ? expected : expected.ResolveLinkTarget(returnFinalTarget: true) ?? expected;
        return resolved is { Exists: true } && destination.Exists && resolved.FullName == destination.FullName;
    }

    private static FileStream Lock()
    {
        string path = OpenXlrPaths.ConfigFile("installation.lock");
        if (InstallationReceipt.HasLinkedParent(path, Root) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("The installation lock path is a symbolic link.");
        OpenXlrPaths.EnsurePrivateDir(OpenXlrPaths.ConfigDir);
        if (File.Exists(path)) RegularFile.Require(path);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
