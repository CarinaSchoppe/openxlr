using System.Diagnostics;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// UCM coexistence. A card that ships a UCM profile (the Wave XLR Pro's
/// splits the device into per-function nodes for daemon-less use) hides
/// the raw multichannel nodes this mixer links against while the split
/// "HiFi" profile is active. The daemon therefore switches such a card
/// to the pro-audio profile while it drives the device, and restores the
/// previous profile on graceful shutdown, so the split serves exactly
/// when OpenXLR is not running. Cards without a UCM profile never match
/// the gate (their active profile is not named "HiFi") and are left
/// alone.
/// </summary>
public static class CardProfile
{
    /// <summary>
    /// If the card whose device.name contains <paramref name="nameFragment"/>
    /// is in a UCM profile and offers pro-audio, switch it there. Returns
    /// the previous profile name when a switch happened, else null.
    /// </summary>
    public static string? EnsureProAudio(string nameFragment)
    {
        var card = FindCard(nameFragment);
        if (card is null) return null;
        (uint id, string active, Dictionary<string, int> profiles) = card.Value;
        if (active is not ("HiFi" or "Direct")) return null;   // not UCM-split; leave alone
        if (!profiles.TryGetValue("pro-audio", out int index)) return null;
        Run("wpctl", "set-profile", id.ToString(), index.ToString());
        return active;
    }

    /// <summary>Set the card back to a named profile (best effort).</summary>
    public static void SetProfile(string nameFragment, string profileName)
    {
        var card = FindCard(nameFragment);
        if (card is null) return;
        (uint id, _, Dictionary<string, int> profiles) = card.Value;
        if (profiles.TryGetValue(profileName, out int index))
            Run("wpctl", "set-profile", id.ToString(), index.ToString());
    }

    private static (uint Id, string Active, Dictionary<string, int> Profiles)? FindCard(string nameFragment)
    {
        string dump;
        try { dump = Run("pw-dump"); }
        catch (Exception) { return null; }
        using JsonDocument doc = JsonDocument.Parse(dump);
        foreach (JsonElement o in doc.RootElement.EnumerateArray())
        {
            if (o.GetProperty("type").GetString() != "PipeWire:Interface:Device") continue;
            JsonElement info = o.GetProperty("info");
            string name = info.GetProperty("props").TryGetProperty("device.name", out JsonElement n)
                ? n.GetString() ?? "" : "";
            if (!name.Contains(nameFragment, StringComparison.Ordinal)) continue;
            if (!info.TryGetProperty("params", out JsonElement pars)) continue;

            var profiles = new Dictionary<string, int>();
            if (pars.TryGetProperty("EnumProfile", out JsonElement list))
                foreach (JsonElement p in list.EnumerateArray())
                    if (p.TryGetProperty("name", out JsonElement pn) && p.TryGetProperty("index", out JsonElement pi))
                        profiles[pn.GetString() ?? ""] = pi.GetInt32();

            string active = "";
            if (pars.TryGetProperty("Profile", out JsonElement act))
                foreach (JsonElement p in act.EnumerateArray())
                    if (p.TryGetProperty("name", out JsonElement an))
                        active = an.GetString() ?? "";

            return (o.GetProperty("id").GetUInt32(), active, profiles);
        }
        return null;
    }

    private static string Run(string cmd, params string[] args)
    {
        var psi = new ProcessStartInfo(cmd) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {cmd}");
        string outText = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{cmd}: exit {p.ExitCode}");
        return outText;
    }
}
