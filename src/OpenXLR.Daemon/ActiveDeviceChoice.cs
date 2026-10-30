using System.Text.Json;
using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>
/// The interface the last setActiveDevice chose, kept in
/// <c>~/.config/openxlr/active-device.json</c> as
/// <c>{"device": "vvvv:pppp@hash"}</c> so the primary drives it again after
/// a restart. A unit without a location is kept by its model id,
/// <c>vvvv:pppp</c>, as setActiveDevice holds it.
/// </summary>
internal static class ActiveDeviceChoice
{
    internal static string PathName => OpenXlrPaths.ConfigFile("active-device.json");

    private sealed record Saved(string? Device);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The saved choice, or null when there is none or the file does not hold one.</summary>
    public static string? Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(PathName)) return null;
            string? device = JsonSerializer.Deserialize<Saved>(File.ReadAllText(PathName), Options)?.Device;
            if (Valid(device)) return device;
            error = "it names no interface";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Keep <paramref name="device"/> as the choice; null on success, else why it was not written.</summary>
    public static string? Save(string device)
    {
        try
        {
            OpenXlrPaths.WriteAtomicJson(PathName, new Saved(device), Options);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    /// <summary>An instance id, or a model id "vvvv:pppp" in lower-case hex.</summary>
    internal static bool Valid(string? device)
        => UsbLocation.IsInstanceId(device)
           || device is { Length: 9 } && device[4] == ':' && device.Where((c, i) => i != 4).All(char.IsAsciiHexDigitLower);

    /// <summary>The product id inside a valid choice.</summary>
    internal static ushort ProductId(string device) => Convert.ToUInt16(device.Substring(5, 4), 16);
}
