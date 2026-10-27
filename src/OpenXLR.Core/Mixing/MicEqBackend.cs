using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What runs one microphone's EQ. There is one model and, in time, two
/// backends: <see cref="SoftwareEqBackend"/> in the native helper, and a
/// device backend for an interface whose own DSP runs the EQ, written once
/// that hardware's protocol is known. Nothing about the device one is
/// guessed here.
/// </summary>
internal interface IMicEqBackend
{
    /// <summary>"software" or "device", as the state reports it.</summary>
    string Kind { get; }
    /// <summary>The whole EQ, at start and on setEq or a recall.</summary>
    void Apply(MicEq eq);
    /// <summary>One existing band, live.</summary>
    void ApplyBand(int band, EqBand value);
    void SetEnabled(bool on);
    /// <summary>The input stops using this backend.</summary>
    void Release();
}

/// <summary>
/// The EQ in the native helper's <c>eq</c> backend: a mono node at the head
/// of a microphone's route, which runs the microphone's noise removal ahead
/// of the EQ (native/denoise.c). Values go down the helper's command pipe;
/// the helper takes them at its next block, keeping its filter state, so a
/// change spawns nothing and builds no node. Only the controls that changed
/// are written.
/// </summary>
internal sealed class SoftwareEqBackend(PipeWireAdapter pw, FilterHandle stage, MicEq applied, MicNoise? noise = null) : IMicEqBackend
{
    /// <summary>
    /// The stage's noise removal meters: the engine that plays (-1 none,
    /// 0 RNNoise, 1 DeepFilterNet), why one asked for does not, and how many
    /// blocks DeepFilterNet answered late.
    /// </summary>
    internal const string ActiveMeter = "denoise_active", StatusMeter = "denoise_status", LateMeter = "denoise_late";

    internal static readonly IReadOnlySet<string> Meters = new HashSet<string>([ActiveMeter, StatusMeter, LateMeter], StringComparer.Ordinal);

    private readonly Dictionary<string, double> _written = Controls(applied, noise ?? MicNoise.Default);
    private MicEq _applied = applied;

    public string Kind => "software";
    public FilterHandle Stage { get; } = stage;
    /// <summary>The node, whose playback port takes the microphone and whose capture port carries it on.</summary>
    public string Node => Stage.SinkName;
    public bool IsAlive => Stage.IsAlive;
    public int BandCount => _applied.Bands.Count;

    /// <summary>The helper's controls for an EQ, by the symbols native/eq.c declares.</summary>
    public static Dictionary<string, double> Controls(MicEq eq)
    {
        var controls = new Dictionary<string, double>(StringComparer.Ordinal) { ["enabled"] = eq.Enabled ? 1 : 0 };
        for (int i = 0; i < eq.Bands.Count; i++)
        {
            EqBand band = eq.Bands[i];
            string prefix = "band" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_";
            controls[prefix + "enabled"] = band.Enabled ? 1 : 0;
            controls[prefix + "type"] = MicEq.TypeIndex(band.Type);
            controls[prefix + "frequency"] = band.Frequency;
            controls[prefix + "gain"] = band.Gain;
            controls[prefix + "qlog10"] = MicEq.QLog10(band.Q);
        }
        return controls;
    }

    /// <summary>The helper's controls for noise removal, by the symbols native/denoise.c declares.</summary>
    public static Dictionary<string, double> Controls(MicNoise noise) => new(StringComparer.Ordinal)
    {
        ["denoise_enabled"] = noise.Enabled ? 1 : 0,
        ["denoise_engine"] = Math.Max(0, MicNoise.EngineIndex(noise.Engine)),
        ["denoise_strength"] = noise.Strength,
    };

    /// <summary>Every control of the stage, which it starts with.</summary>
    public static Dictionary<string, double> Controls(MicEq eq, MicNoise noise)
    {
        Dictionary<string, double> controls = Controls(eq);
        foreach ((string symbol, double value) in Controls(noise)) controls[symbol] = value;
        return controls;
    }

    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void Apply(MicEq eq)
    {
        Write(Controls(eq));
        _applied = eq;
    }

    /// <summary>The microphone's noise removal, live.</summary>
    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void ApplyNoise(MicNoise noise) => Write(Controls(noise));

    private void Write(Dictionary<string, double> controls)
    {
        foreach ((string symbol, double value) in controls)
        {
            if (_written.TryGetValue(symbol, out double was) && was == value) continue;
            Stage.NativeHost!.SetControl(symbol, value);
            _written[symbol] = value;
        }
    }

    public void ApplyBand(int band, EqBand value) => Apply(_applied.WithBand(band, value));

    public void SetEnabled(bool on) => Apply(_applied with { Enabled = on });

    public void Release() => pw.StopFilter(Stage);
}
