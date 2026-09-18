using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenXLR.UI;

/// <summary>Desktop integration is opt-in, independent of audio profiles.</summary>
internal sealed record DesktopKeySettings
{
    public bool Enabled { get; init; }
    public List<string> FocusChannels { get; init; } = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string FilePath => OpenXlrPaths.ConfigFile("desktop-keys.json");
    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 36 } && id[0] is >= 'a' and <= 'z'
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
    internal DesktopKeySettings Normalize() => this with
    {
        FocusChannels = [.. (FocusChannels ?? []).Where(ValidId).Distinct(StringComparer.Ordinal).Take(32)],
    };
    internal static DesktopKeySettings Load()
    {
        try { return (JsonSerializer.Deserialize<DesktopKeySettings>(File.ReadAllText(FilePath), Json) ?? new()).Normalize(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save() => OpenXlrPaths.WriteAtomicJson(FilePath, Normalize(), Json);
}
