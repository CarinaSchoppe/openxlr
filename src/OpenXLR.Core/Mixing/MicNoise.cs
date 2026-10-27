using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

// Shared with the window and the terminal mixer, which offer the same
// engines, range and steps the daemon takes.
#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// A microphone's noise removal: an on switch, the engine and the strength.
/// It runs in the native helper's microphone stage ahead of the EQ, on
/// XLR 1, XLR 2 and every capture input on one mono port. Off by default, so
/// an upgrade changes nobody's sound.
/// </summary>
public sealed record MicNoise
{
    /// <summary>The engines, in the order the native stage numbers them.</summary>
    public const string RnNoise = "rnnoise", DeepFilterNet = "deepfilternet";

    public static readonly IReadOnlyList<string> Engines = [RnNoise, DeepFilterNet];

    /// <summary>
    /// Strength is the most the noise is turned down, in dB, on whole
    /// decibels; at <see cref="MaxStrength"/> the engine works unlimited.
    /// </summary>
    public const double MinStrength = 0, MaxStrength = 100, StrengthStep = 1, DefaultStrength = 100;

    public bool Enabled { get; init; }
    /// <summary>One of <see cref="Engines"/>.</summary>
    public string Engine { get; init; } = RnNoise;
    public double Strength { get; init; } = DefaultStrength;

    /// <summary>Off, RNNoise, unlimited.</summary>
    public static MicNoise Default { get; } = new();

    /// <summary>The native stage's number for an engine, or -1 for an unknown one.</summary>
    public static int EngineIndex(string? engine)
    {
        for (int i = 0; i < Engines.Count; i++)
            if (Engines[i] == engine) return i;
        return -1;
    }

    public static double SnapStrength(double db) => Math.Round(db / StrengthStep, MidpointRounding.AwayFromZero) * StrengthStep;

    public static MicNoise Snap(MicNoise noise) => noise with { Strength = SnapStrength(noise.Strength) };

    /// <summary>
    /// Why a setting cannot be taken, or null. Values outside the range are
    /// refused, never clamped, and the reason is written the same way
    /// whatever the daemon's locale.
    /// </summary>
    public static string? Check(MicNoise? noise)
    {
        if (noise is null) return "need 'enabled', 'engine' and 'strength'";
        if (EngineIndex(noise.Engine) < 0) return $"engine must be one of {string.Join(", ", Engines)}";
        if (!double.IsFinite(noise.Strength) || noise.Strength is < MinStrength or > MaxStrength)
            return string.Create(CultureInfo.InvariantCulture, $"strength must be {MinStrength} to {MaxStrength} dB");
        return null;
    }

    /// <summary>
    /// Read a whole setting from an object with <c>enabled</c>, <c>engine</c>
    /// and <c>strength</c>, as a profile or a state push holds it. Null with
    /// <paramref name="error"/> set when it cannot be taken.
    /// </summary>
    public static MicNoise? Read(JsonElement json, out string? error)
    {
        error = null;
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !json.TryGetProperty("engine", out JsonElement engine) || engine.ValueKind != JsonValueKind.String
            || !json.TryGetProperty("strength", out JsonElement strength) || strength.ValueKind != JsonValueKind.Number
            || !strength.TryGetDouble(out double db))
        {
            error = "need 'enabled', 'engine' and 'strength'";
            return null;
        }
        var value = new MicNoise { Enabled = enabled.GetBoolean(), Engine = engine.GetString()!, Strength = db };
        if (Check(value) is string problem)
        {
            error = problem;
            return null;
        }
        return Snap(value);
    }
}

/// <summary>
/// Part of a noise removal setting, as <c>setNoiseRemoval</c> carries it:
/// each field present replaces the setting's, each absent one keeps it.
/// </summary>
public sealed record MicNoiseChange(bool? Enabled = null, string? Engine = null, double? Strength = null)
{
    public bool IsEmpty => Enabled is null && Engine is null && Strength is null;

    /// <summary>The setting with this change, unsnapped; check and snap the result.</summary>
    public MicNoise ApplyTo(MicNoise noise) => noise with
    {
        Enabled = Enabled ?? noise.Enabled,
        Engine = Engine ?? noise.Engine,
        Strength = Strength ?? noise.Strength,
    };
}

/// <summary>
/// A microphone's noise removal as clients see it: the setting, the engines
/// this installation's native helper carries, the engine running now (null
/// while none runs), the delay it adds, whether noise removal can run at
/// all, and why what was asked for does not run.
/// </summary>
public sealed record MicNoiseState(bool Enabled, string Engine, double Strength)
{
    /// <summary>What the helper was built with, from <see cref="Engines"/>; empty without the helper.</summary>
    public IReadOnlyList<string> Engines { get; init; } = [];

    /// <summary>The engine whose output plays now, or null.</summary>
    public string? Active { get; init; }

    /// <summary>The delay the running engine adds, in milliseconds; null while none runs.</summary>
    public double? LatencyMs { get; init; }

    /// <summary>The native helper is installed, so noise removal can be switched on.</summary>
    public bool Available { get; init; }

    /// <summary>Why the setting does not run as asked, or null.</summary>
    public string? Error { get; init; }
}
