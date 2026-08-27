using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenXLR.Core.Devices;

/// <summary>
/// The Elgato XLR Dock (0fd9:00a6), the controls-free Stream Deck+ module.
///
/// Driven entirely through the kernel's standard ALSA controls instead of the
/// vendor block protocol: the dock is a software-defined device whose gain,
/// mute, and headphone volume the kernel already exposes ('Mic Capture
/// Volume' 0..150 for 0..75 dB, 'Mic Capture Switch', 'PCM Playback Volume'
/// 0..120 for -60..0 dB), backed by the same registers Wave Link drives.
/// This backend therefore opens no USB handle and sends no vendor traffic,
/// so nothing here can disturb the audio streams.
///
/// The device has no physical controls, so nothing changes state behind our
/// back except other ALSA clients; state reads are cached briefly to keep the
/// daemon's poll loop from spawning amixer ten times a second.
/// </summary>
public sealed class XlrDockDevice : IAudioDevice
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00A6;

    private const string GainCtl = "Mic Capture Volume";
    private const string MuteCtl = "Mic Capture Switch";
    private const string HpCtl = "PCM Playback Volume";

    private int _card = -1;
    private DeviceState? _cached;
    private DateTime _cachedAt;
    private readonly object _lock = new();

    public DeviceInfo Info { get; } = new("Elgato", "XLR Dock", VendorId, ProductId);

    public DeviceCapabilities Capabilities { get; } = new()
    {
        Gain = true,
        Mute = true,
        HpVolume = true,
        XlrInputs = 1,
        HpOutputs = 1,
    };

    public bool Connected => _card >= 0;

    public void Connect()
    {
        foreach (string dir in Directory.EnumerateDirectories("/proc/asound").OrderBy(d => d))
        {
            string usbid = Path.Combine(dir, "usbid");
            try
            {
                if (File.Exists(usbid) && File.ReadAllText(usbid).Trim() == "0fd9:00a6"
                    && int.TryParse(Path.GetFileName(dir).Replace("card", ""), out int n))
                {
                    _card = n;
                    return;
                }
            }
            catch (IOException) { /* card went away mid-scan */ }
        }
        throw new InvalidOperationException("XLR Dock present on USB but its ALSA card was not found");
    }

    public void Disconnect() => _card = -1;

    private string Amixer(params string[] args)
    {
        var psi = new ProcessStartInfo("amixer")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(_card.ToString());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start amixer");
        string outText = p.StandardOutput.ReadToEnd();
        p.WaitForExit(2000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"amixer {string.Join(' ', args)}: exit {p.ExitCode}");
        return outText;
    }

    private static readonly Regex Values = new(@": values=([A-Za-z0-9,\-]+)", RegexOptions.Compiled);

    private string Get(string name)
    {
        Match m = Values.Match(Amixer("cget", $"name={name}"));
        if (!m.Success) throw new InvalidOperationException($"amixer cget '{name}': no values");
        return m.Groups[1].Value;
    }

    private void Set(string name, string value)
    {
        lock (_lock)
        {
            Amixer("cset", $"name={name}", value);
            _cached = null;   // next ReadState reflects the write immediately
        }
    }

    public DeviceState ReadState()
    {
        lock (_lock)
        {
            if (_cached is not null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 1)
                return _cached;
            int gainRaw = int.Parse(Get(GainCtl));
            bool unmuted = Get(MuteCtl).StartsWith("on");
            int hpRaw = int.Parse(Get(HpCtl));
            _cached = new DeviceState
            {
                GainDb = (int)Math.Round(gainRaw / 2.0),
                Mute = !unmuted,
                HpVolumeDb = hpRaw / 2.0 - 60.0,
                Crossfade = 100,   // not a hardware feature here; neutral centre
            };
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    public void SetGainDb(int db) => Set(GainCtl, (Math.Clamp(db, 0, 75) * 2).ToString());

    public void SetMute(bool on) => Set(MuteCtl, on ? "off" : "on");

    public void SetHpVolumeDb(double db)
        => Set(HpCtl, ((int)Math.Round((Math.Clamp(db, -60.0, 0.0) + 60.0) * 2)).ToString());

    // Everything else runs host-side (Wave Link style) or does not exist on
    // this hardware; the capabilities above keep the UI from offering them.
    public void SetLowCut(bool on) { }
    public void SetExpander(bool on) { }
    public void SetVoiceTune(bool on) { }
    public void SetVoiceTuneStrength(int value) { }
    public void SetLowImpedance(bool on) { }
    public void SetCrossfade(int value) { }
    public void SetPhantom(bool on) { }
    public void SetClipGuard(bool on) { }
    public void SetCompressor(bool on) { }

    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        try
        {
            return new Dictionary<string, string>
            {
                ["alsa"] = $"card={_card} gain={Get(GainCtl)} capture={Get(MuteCtl)} hp={Get(HpCtl)}",
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, string> { ["alsa"] = $"error: {ex.Message}" };
        }
    }
}
