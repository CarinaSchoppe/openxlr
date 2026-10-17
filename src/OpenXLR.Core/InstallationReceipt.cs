using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

internal sealed record InstalledFile
{
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "file";
    public string? Sha256 { get; init; }
    public string? LinkTarget { get; init; }
    public int? Mode { get; init; }
    public string? ReplacedSha256 { get; init; }
    public int? ReplacedMode { get; init; }
    public bool Optional { get; init; }
    public bool Removed { get; init; }
}

/// <summary>Files owned by one package or by the window's startup integration.</summary>
internal sealed record InstallationReceipt
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, MaxDepth = 16 };
    public int FormatVersion { get; init; } = 1;
    public string Package { get; init; } = "";
    public string Manager { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Commit { get; init; }
    public bool Dirty { get; init; }
    public string Prefix { get; init; } = "";
    public List<InstalledFile> Files { get; init; } = [];

    internal static InstallationReceipt Read(string path, string? root = null, bool archived = false)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("The installation receipt is a symbolic link.");
        byte[] bytes = RegularFile.ReadBytes(path, MaxBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            CheckNames(document.RootElement);
            var receipt = document.RootElement.Deserialize<InstallationReceipt>(Json)
                ?? throw new JsonException("Receipt is not an object.");
            receipt.Validate();
            string ownPath = receipt.Manager == "user" ? System.IO.Path.Combine(receipt.Prefix, "openxlr", "installation.json") :
                System.IO.Path.Combine(receipt.Prefix, "share", "openxlr", "installations", receipt.Package + ".json");
            string actual = root is null ? System.IO.Path.GetFullPath(path) :
                "/" + System.IO.Path.GetRelativePath(root, path);
            if (receipt.Files.Count(f => f.Kind == "receipt" && f.Path == ownPath) != 1 ||
                receipt.Files.Any(f => f.Kind == "receipt" && f.Path != ownPath) || !archived && actual != ownPath)
                throw new IOException("The receipt's own path does not match its location.");
            if (archived && System.IO.Path.GetFileName(path) != Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".json")
                throw new IOException("The archived receipt's content hash does not match its filename.");
            return receipt;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new IOException("Invalid installation receipt: " + ex.Message, ex); }
    }

    private static void CheckNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate property: " + property.Name);
                CheckNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) CheckNames(item);
    }

    internal void Validate()
    {
        if (FormatVersion != 1 || Package is null || Package.Length is 0 or > 80 ||
            !char.IsAsciiLetterOrDigit(Package[0]) || !Package.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '.') ||
            Manager is not ("user" or "debian" or "rpm" or "arch" or "nix" or "manual") ||
            Manager == "manual" && (Package != "openxlr-yabridge" || Prefix != "/usr/local") ||
            Version is null || Version.Length is 0 or > 128 || Version.Any(char.IsControl) ||
            Commit is not null && (Commit.Length != 40 || !Commit.All(char.IsAsciiHexDigit)) ||
            !Absolute(Prefix) || Prefix == System.IO.Path.GetPathRoot(Prefix) ||
            Files is null || Files.Count > 8192)
            throw new IOException("Invalid installation receipt header.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (InstalledFile? file in Files)
        {
            if (file is null || !Absolute(file.Path) ||
                !file.Path.StartsWith(Prefix.TrimEnd('/') + '/', StringComparison.Ordinal) || !paths.Add(file.Path) ||
                file.Kind is not ("file" or "link" or "receipt") ||
                file.Kind == "file" && (!Hash(file.Sha256) || file.LinkTarget is not null) ||
                file.Kind == "link" && (file.Sha256 is not null || file.Mode is not null || string.IsNullOrEmpty(file.LinkTarget) || file.LinkTarget.Length > 4096 || file.LinkTarget.Any(char.IsControl)) ||
                file.Kind == "receipt" && (file.Sha256 is not null || file.LinkTarget is not null) ||
                file.Mode is < 0 or > 511 || file.ReplacedSha256 is not null && !Hash(file.ReplacedSha256) ||
                file.ReplacedMode is < 0 or > 511 || file.ReplacedMode is not null && file.ReplacedSha256 is null || file.ReplacedSha256 is not null && (Manager != "user" || file.Kind != "file") ||
                file.Removed && Manager != "user" || file.Optional && (Manager == "user" || file.Kind != "file" ||
                    !(file.Path.StartsWith(Prefix + "/share/doc/", StringComparison.Ordinal) || file.Path.StartsWith(Prefix + "/share/licenses/", StringComparison.Ordinal))))
                throw new IOException("Invalid or repeated owned path.");
        }
        foreach (InstalledFile file in Files.Where(f => f.ReplacedSha256 is not null))
            if (!Files.Any(f => f.Kind == "file" && !f.Removed && f.Sha256 == file.ReplacedSha256 &&
                f.Path == System.IO.Path.Combine(Prefix, "openxlr", "installation-backups", file.ReplacedSha256!)))
                throw new IOException("The original file backup is not recorded.");
    }

    private static bool Absolute(string? value) => value is { Length: > 1 and <= 4096 } &&
        System.IO.Path.IsPathFullyQualified(value) && !value.Any(char.IsControl) &&
        System.IO.Path.GetFullPath(value) == value && !value.EndsWith('/');
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string Digest(string path)
    {
        RegularFile.Require(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 512L * 1024 * 1024) throw new IOException("Owned file exceeds the 512 MiB inspection limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if ((total += read) > 512L * 1024 * 1024) throw new IOException("Owned file exceeds the 512 MiB inspection limit.");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static string? Problem(InstalledFile file, string prefix, string? root = null)
    {
        string path = root is null ? file.Path : System.IO.Path.Combine(root, file.Path.TrimStart('/'));
        // The installation prefix is trusted on the live machine, but inside a
        // staging tree its parents, including the prefix itself, must stay inside root.
        string boundary = root ?? prefix;
        try
        {
            if (HasLinkedParent(path, boundary)) return "parent is a symbolic link";
            var info = new FileInfo(path);
            if (Directory.Exists(path) && info.LinkTarget is null) return "path is a directory";
            if (file.Removed)
            {
                bool leftover = file.Kind == "link" ? SameLink(file.Path, info.LinkTarget, file.LinkTarget) :
                    info.LinkTarget is null && info.Exists && file.ReplacedSha256 is null && file.Kind == "file" && file.Sha256 == Digest(path);
                return leftover ? "left behind after removal" : null;
            }
            if (info.LinkTarget is { } target)
                return file.Kind == "link" && SameLink(file.Path, target, file.LinkTarget) ? null : "symbolic link was modified";
            if (!info.Exists) return file.Optional ? null : "missing";
            if (file.Kind == "link") return "symbolic link was replaced";
            if (file.Kind != "receipt" && Digest(path) != file.Sha256) return "contents were modified";
            if (!OperatingSystem.IsWindows() && file.Mode is int mode && (int)File.GetUnixFileMode(path) != mode)
                return "permissions were modified";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "unreadable: " + ex.Message; }
    }

    // Nix rewrites absolute links to relative ones after postFixup. Compare
    // their lexical destinations, without following links out of a staged tree.
    private static bool SameLink(string path, string? actual, string? expected) =>
        actual is not null && expected is not null &&
        System.IO.Path.GetFullPath(actual, System.IO.Path.GetDirectoryName(path)!) ==
        System.IO.Path.GetFullPath(expected, System.IO.Path.GetDirectoryName(path)!);

    internal static bool HasLinkedParent(string path, string boundary)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(path)!);
        while (directory.FullName != boundary && directory.FullName.StartsWith(boundary.TrimEnd('/') + '/', StringComparison.Ordinal))
        {
            if (directory.LinkTarget is not null) return true;
            if (directory.Parent is not { } parent) break;
            directory = parent;
        }
        return false;
    }
}
