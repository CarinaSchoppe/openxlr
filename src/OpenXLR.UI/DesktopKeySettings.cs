using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;

namespace OpenXLR.UI;

/// <summary>Desktop integration is opt-in, independent of audio profiles.</summary>
internal sealed record DesktopKeySettings
{
    public bool Enabled { get; init; }
    public List<string> FocusChannels { get; init; } = [];
    public bool OutputControls { get; init; }
    public string? OutputDevice { get; init; }
    public List<string> MainOutputs { get; init; } = [];
    internal static string MainKey(string device) => "main_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device)))[..24].ToLowerInvariant();
    internal static bool ValidOutput(string? name) => name is { Length: > 0 and <= 256 } && !name.Any(char.IsControl)
        && (name == "@monitor" || (!name.Contains('#') && name[0] is not ('@' or '-') && !name.All(char.IsAsciiDigit)));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string FilePath => OpenXlrPaths.ConfigFile("desktop-keys.json");
    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 36 } && id[0] is >= 'a' and <= 'z'
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
    internal DesktopKeySettings Normalize() => this with
    {
        FocusChannels = [.. (FocusChannels ?? []).Where(ValidId).Distinct(StringComparer.Ordinal).Take(32)],
        MainOutputs = [.. (MainOutputs ?? []).Where(ValidOutput).Distinct(StringComparer.Ordinal).Take(16)],
        OutputControls = OutputControls && (OutputDevice is null || (ValidOutput(OutputDevice) && OutputDevice != "@monitor")),
    };
    internal static DesktopKeySettings Load()
    {
        try { return (JsonSerializer.Deserialize<DesktopKeySettings>(File.ReadAllText(FilePath), Json) ?? new()).Normalize(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save() => OpenXlrPaths.WriteAtomicJson(FilePath, Normalize(), Json);
}
