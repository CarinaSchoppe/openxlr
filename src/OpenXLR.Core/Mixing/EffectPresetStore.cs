using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>A portable, versioned snapshot of an entire insert chain.</summary>
public sealed record EffectChainPreset
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Name { get; init; }
    public required List<InsertDefinition> Inserts { get; init; }
}

/// <summary>A portable state snapshot for one plugin instance.</summary>
public sealed record PluginPreset
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Name { get; init; }
    public required InsertDefinition Insert { get; init; }
}

/// <summary>
/// Local preset storage with bounded, schema-validated import/export. Names
/// become sanitized file names; callers never supply a filesystem path.
/// </summary>
public static class EffectPresetStore
{
    public const int MaxDocumentBytes = 1024 * 1024;
    public const int MaxNativeStateBytes = 512 * 1024;
    public const int MaxInserts = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<string> ListChains(string? root = null)
        => List(ChainDirectory(root));

    public static IReadOnlyList<string> ListPlugins(string? root = null)
        => List(PluginDirectory(root));

    public static void SaveChain(EffectChainPreset preset, string? root = null)
    {
        Validate(preset);
        Save(PathFor(ChainDirectory(root), preset.Name), preset);
    }

    public static void SavePlugin(PluginPreset preset, string? root = null)
    {
        Validate(preset);
        Save(PathFor(PluginDirectory(root), preset.Name), preset);
    }

    public static EffectChainPreset? LoadChain(string name, string? root = null)
        => Load<EffectChainPreset>(PathFor(ChainDirectory(root), name), Validate);

    public static PluginPreset? LoadPlugin(string name, string? root = null)
        => Load<PluginPreset>(PathFor(PluginDirectory(root), name), Validate);

    public static bool DeleteChain(string name, string? root = null)
        => Delete(PathFor(ChainDirectory(root), name));

    public static bool DeletePlugin(string name, string? root = null)
        => Delete(PathFor(PluginDirectory(root), name));

    public static void RenameChain(string oldName, string newName, string? root = null)
    {
        EffectChainPreset source = LoadChain(oldName, root)
            ?? throw new InvalidOperationException($"no chain preset named '{oldName}'");
        if (NormalizeName(oldName) == NormalizeName(newName)) return;
        SaveChain(source with { Name = NormalizeName(newName) }, root);
        DeleteChain(oldName, root);
    }

    public static void DuplicateChain(string sourceName, string copyName, string? root = null)
    {
        EffectChainPreset source = LoadChain(sourceName, root)
            ?? throw new InvalidOperationException($"no chain preset named '{sourceName}'");
        SaveChain(source with
        {
            Name = NormalizeName(copyName),
            Inserts = Clone(source.Inserts),
        }, root);
    }

    public static void RenamePlugin(string oldName, string newName, string? root = null)
    {
        PluginPreset source = LoadPlugin(oldName, root)
            ?? throw new InvalidOperationException($"no plugin preset named '{oldName}'");
        if (NormalizeName(oldName) == NormalizeName(newName)) return;
        SavePlugin(source with { Name = NormalizeName(newName) }, root);
        DeletePlugin(oldName, root);
    }

    public static void DuplicatePlugin(string sourceName, string copyName, string? root = null)
    {
        PluginPreset source = LoadPlugin(sourceName, root)
            ?? throw new InvalidOperationException($"no plugin preset named '{sourceName}'");
        SavePlugin(source with { Name = NormalizeName(copyName), Insert = Clone(source.Insert) }, root);
    }

    public static byte[] ExportChain(string name, string? root = null)
    {
        EffectChainPreset preset = LoadChain(name, root)
            ?? throw new InvalidOperationException($"no chain preset named '{name}'");
        return JsonSerializer.SerializeToUtf8Bytes(preset, Json);
    }

    public static byte[] ExportPlugin(string name, string? root = null)
    {
        PluginPreset preset = LoadPlugin(name, root)
            ?? throw new InvalidOperationException($"no plugin preset named '{name}'");
        return JsonSerializer.SerializeToUtf8Bytes(preset, Json);
    }

    public static EffectChainPreset ImportChain(ReadOnlySpan<byte> document, string? root = null)
    {
        EffectChainPreset preset = ParseImported<EffectChainPreset>(document);
        Validate(preset);
        SaveChain(preset, root);
        return preset;
    }

    public static PluginPreset ImportPlugin(ReadOnlySpan<byte> document, string? root = null)
    {
        PluginPreset preset = ParseImported<PluginPreset>(document);
        Validate(preset);
        SavePlugin(preset, root);
        return preset;
    }

    private static T ParseImported<T>(ReadOnlySpan<byte> document)
    {
        if (document.Length is 0 or > MaxDocumentBytes)
            throw new InvalidOperationException($"preset document must be 1 to {MaxDocumentBytes} bytes");
        try
        {
            return JsonSerializer.Deserialize<T>(document, Json)
                ?? throw new InvalidOperationException("preset document is empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"invalid preset JSON: {ex.Message}", ex);
        }
    }

    public static string NormalizeName(string? name)
    {
        string clean = name?.Trim() ?? "";
        if (clean.Length is 0 or > 80 || clean.Any(char.IsControl)
            || clean is "." or ".." || clean.Contains('/') || clean.Contains('\\'))
            throw new InvalidOperationException("preset name must contain 1 to 80 safe printable characters");
        return clean;
    }

    public static void Validate(EffectChainPreset preset)
    {
        if (preset.SchemaVersion != EffectChainPreset.CurrentSchemaVersion)
            throw new InvalidOperationException($"unsupported chain preset schema {preset.SchemaVersion}");
        NormalizeName(preset.Name);
        if (preset.Inserts is null || preset.Inserts.Any(insert => insert is null))
            throw new InvalidOperationException("preset inserts must be a non-null array of objects");
        if (preset.Inserts.Count > MaxInserts)
            throw new InvalidOperationException($"a chain preset may contain at most {MaxInserts} inserts");
        if (preset.Inserts.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != preset.Inserts.Count)
            throw new InvalidOperationException("insert IDs must be unique within a chain preset");
        foreach (InsertDefinition insert in preset.Inserts) Validate(insert);
    }

    public static void Validate(PluginPreset preset)
    {
        if (preset.SchemaVersion != PluginPreset.CurrentSchemaVersion)
            throw new InvalidOperationException($"unsupported plugin preset schema {preset.SchemaVersion}");
        NormalizeName(preset.Name);
        Validate(preset.Insert);
    }

    public static void Validate(InsertDefinition insert)
    {
        if (insert is null || insert.Params is null || insert.Sidechains is null)
            throw new InvalidOperationException("insert and its parameter/sidechain maps must not be null");
        if (string.IsNullOrWhiteSpace(insert.Id) || insert.Id.Length > 100)
            throw new InvalidOperationException("insert ID is missing or too long");
        if (insert.Kind is not ("lv2" or "vst3"))
            throw new InvalidOperationException($"unsupported plugin format '{insert.Kind}'");
        if (string.IsNullOrWhiteSpace(insert.Plugin) || insert.Plugin.Length > 4096)
            throw new InvalidOperationException("plugin identity is missing or too long");
        if (insert.Params.Count > 4096 || insert.Params.Any(p =>
                string.IsNullOrWhiteSpace(p.Key) || p.Key.Any(char.IsWhiteSpace) || p.Key.Any(char.IsControl)
                || p.Key.Length > 200 || !double.IsFinite(p.Value)))
            throw new InvalidOperationException("plugin parameters are invalid or exceed the limit");
        if (insert.Sidechains.Count > 32 || insert.Sidechains.Any(route =>
                string.IsNullOrWhiteSpace(route.Key) || string.IsNullOrWhiteSpace(route.Value)
                || route.Key.Length > 200 || route.Value.Length > 200))
            throw new InvalidOperationException("plugin sidechain routes are invalid or exceed the limit");
        if (insert.State is not null)
        {
            try
            {
                int bytes = Convert.FromBase64String(insert.State).Length;
                if (bytes > MaxNativeStateBytes)
                    throw new InvalidOperationException($"plugin state exceeds {MaxNativeStateBytes} bytes");
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("plugin state is not valid base64", ex);
            }
        }
    }

    private static InsertDefinition Clone(InsertDefinition insert)
        => insert with
        {
            Params = new Dictionary<string, double>(insert.Params),
            Sidechains = new Dictionary<string, string>(insert.Sidechains),
        };

    private static List<InsertDefinition> Clone(IEnumerable<InsertDefinition> source)
        => [.. source.Select(Clone)];

    private static IReadOnlyList<string> List(string directory)
    {
        try
        {
            return [.. Directory.EnumerateFiles(directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null).Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static string Root(string? root) => root ?? Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "openxlr", "presets");
    private static string ChainDirectory(string? root) => Path.Combine(Root(root), "chains");
    private static string PluginDirectory(string? root) => Path.Combine(Root(root), "plugins");
    private static string PathFor(string directory, string name)
        => Path.Combine(directory, NormalizeName(name) + ".json");

    private static void Save<T>(string path, T value)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (data.Length > MaxDocumentBytes)
            throw new InvalidOperationException($"preset document exceeds {MaxDocumentBytes} bytes");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, data);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static T? Load<T>(string path, Action<T> validate)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            if (file.Length > MaxDocumentBytes)
                throw new InvalidOperationException($"preset document exceeds {MaxDocumentBytes} bytes");
            T result = JsonSerializer.Deserialize<T>(file, Json)
                ?? throw new InvalidOperationException("preset document is empty");
            validate(result);
            return result;
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"invalid preset JSON: {ex.Message}", ex);
        }
    }

    private static bool Delete(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
