using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>The command noise removal sends. The daemon client sends it; a test records it.</summary>
public interface INoiseCommands
{
    /// <summary>Change part of a microphone's noise removal; a null field keeps its value.</summary>
    Task SetNoiseRemovalAsync(string channel, bool? enabled = null, string? engine = null, double? strength = null);
}

/// <summary>
/// A microphone's noise removal as Mic setup edits it: the switch, the
/// engine, the strength, and what the daemon says runs. Every value is one
/// the daemon reported or one the user just set; a report that arrives while
/// an edit is still on its way does not move the control back.
/// </summary>
public sealed class MicNoiseViewModel : ViewModelBase
{
    /// <summary>How long a recent edit wins over state reports that may still precede it.</summary>
    internal static readonly TimeSpan EchoWait = TimeSpan.FromSeconds(1);

    private readonly INoiseCommands _commands;
    private readonly TimeProvider _time;
    private bool _enabled, _available, _detached;
    private string _engine = MicNoise.RnNoise;
    private double _strength = MicNoise.DefaultStrength;
    private IReadOnlyList<string> _engines = [];
    private string? _active, _error;
    private double? _latencyMs;
    private string _channelName;
    // When the user last moved each control, so a stale report does not undo it.
    private long _enabledAt = long.MinValue, _engineAt = long.MinValue, _strengthAt = long.MinValue;

    public MicNoiseViewModel(INoiseCommands commands, string channel, string channelName, TimeProvider? time = null)
    {
        _commands = commands;
        Channel = channel;
        _channelName = channelName;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The channel id the command names.</summary>
    public string Channel { get; }

    public string ChannelName
    {
        get => _channelName;
        internal set
        {
            if (!Set(ref _channelName, value)) return;
            Raise(nameof(SwitchName));
            Raise(nameof(ButtonName));
        }
    }

    /// <summary>The switch's accessible name, with the channel in it.</summary>
    public string SwitchName => Localizer.Format("NoiseRemovalSwitchName", _channelName);

    /// <summary>Noise removal's switch.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_detached || !Set(ref _enabled, value)) return;
            _enabledAt = _time.GetTimestamp();
            _ = _commands.SetNoiseRemovalAsync(Channel, enabled: value);
            RaiseStatus();
        }
    }

    /// <summary>The engine chosen: <c>rnnoise</c> or <c>deepfilternet</c>.</summary>
    public string Engine => _engine;

    public bool IsRnNoise => _engine == MicNoise.RnNoise;
    public bool IsDeepFilterNet => _engine == MicNoise.DeepFilterNet;

    /// <summary>The native helper carries DeepFilterNet, so it can be chosen.</summary>
    public bool HasDeepFilterNet => _engines.Contains(MicNoise.DeepFilterNet);

    /// <summary>The native helper is installed, so the controls do something.</summary>
    public bool Available => _available;

    /// <summary>Choose an engine the helper carries; any other is ignored.</summary>
    public void ChooseEngine(string engine)
    {
        if (_detached) return;
        if (engine == _engine || MicNoise.EngineIndex(engine) < 0 || !_engines.Contains(engine))
        {
            RaiseEngine();   // a press on the lit side leaves it lit
            return;
        }
        _engine = engine;
        _engineAt = _time.GetTimestamp();
        _ = _commands.SetNoiseRemovalAsync(Channel, engine: engine);
        RaiseEngine();
        RaiseStatus();
    }

    /// <summary>The most the noise is turned down, in whole decibels.</summary>
    public double Strength
    {
        get => _strength;
        set
        {
            if (_detached) return;
            double snapped = Math.Clamp(MicNoise.SnapStrength(value), MicNoise.MinStrength, MicNoise.MaxStrength);
            if (!Set(ref _strength, snapped)) return;
            _strengthAt = _time.GetTimestamp();
            SliderSync.Send($"noise:{Channel}", () => _ = _commands.SetNoiseRemovalAsync(Channel, strength: snapped));
            Raise(nameof(StrengthText));
        }
    }

    public double MinStrength => MicNoise.MinStrength;
    public double MaxStrength => MicNoise.MaxStrength;

    /// <summary>The strength as the slider's value label: "30 dB", or "No limit" at the top.</summary>
    public string StrengthText => _strength >= MicNoise.MaxStrength
        ? Localizer.Text("NoiseRemovalUnlimited")
        : Localizer.Format("NoiseRemovalStrengthValue", (int)_strength);

    /// <summary>
    /// One line under the controls: why the setting does not run, else what
    /// runs and the delay it adds, else nothing while it is off.
    /// </summary>
    public string Status
    {
        get
        {
            if (!_enabled) return "";
            if (_error is { Length: > 0 } error) return error;
            if (_active is null || _latencyMs is not double ms)
                return _engine == MicNoise.DeepFilterNet ? Localizer.Text("NoiseRemovalLoading") : Localizer.Text("NoiseRemovalStarting");
            return Localizer.Format("NoiseRemovalRunning", EngineName(_active), Math.Round(ms).ToString(CultureInfo.CurrentCulture));
        }
    }

    public bool HasStatus => Status.Length > 0;

    /// <summary>True while the line is an error rather than a report.</summary>
    public bool HasError => _enabled && _error is { Length: > 0 };

    /// <summary>True while the line is a report: what runs, or that it is starting.</summary>
    public bool ShowsReport => HasStatus && !HasError;

    /// <summary>The helper runs but was built without DeepFilterNet, which is then offered greyed out with a note.</summary>
    public bool LacksDeepFilterNet => _available && !HasDeepFilterNet;

    /// <summary>Noise removal runs: the Denoise button and the matrix's processing button light.</summary>
    public bool IsLit => _enabled && _available && _error is not { Length: > 0 };

    /// <summary>Noise removal is on and cannot run: the Denoise button takes the alert colour.</summary>
    public bool IsFaulty => _enabled && (!_available || _error is { Length: > 0 });

    /// <summary>The Denoise button's name for a screen reader: whose noise removal, and its state.</summary>
    public string ButtonName => IsFaulty ? Localizer.Format("NoiseButtonNameUnavailable", _channelName)
        : _enabled ? Localizer.Format("NoiseButtonNameOn", _channelName) : Localizer.Format("NoiseButtonNameOff", _channelName);

    private static string EngineName(string engine)
        => engine == MicNoise.DeepFilterNet ? Localizer.Text("NoiseRemovalDeepFilterNet") : Localizer.Text("NoiseRemovalRnNoise");

    private bool Settled(long at) => at == long.MinValue || _time.GetElapsedTime(at) >= EchoWait;

    /// <summary>Take the channel's <c>noise</c> from a state push.</summary>
    internal void Apply(JsonNode? noise)
    {
        if (_detached || noise is not JsonObject n) return;
        _engines = n["engines"] is JsonArray list
            ? [.. list.Select(e => e?.GetValue<string>()).OfType<string>()]
            : [];
        _available = n["available"]?.GetValue<bool>() ?? false;
        _active = n["active"]?.GetValue<string>();
        _latencyMs = n["latencyMs"]?.GetValue<double>();
        _error = n["error"]?.GetValue<string>();
        if (Settled(_enabledAt) && n["enabled"]?.GetValue<bool>() is bool enabled && enabled != _enabled)
        {
            _enabled = enabled;
            Raise(nameof(Enabled));
        }
        if (Settled(_engineAt) && n["engine"]?.GetValue<string>() is string engine && engine != _engine && MicNoise.EngineIndex(engine) >= 0)
            _engine = engine;
        if (Settled(_strengthAt) && !SliderSync.RecentlyTouched($"noise:{Channel}")
            && n["strength"]?.GetValue<double>() is double strength && strength != _strength)
        {
            _strength = strength;
            Raise(nameof(Strength));
            Raise(nameof(StrengthText));
        }
        Raise(nameof(Available));
        Raise(nameof(HasDeepFilterNet));
        Raise(nameof(LacksDeepFilterNet));
        RaiseEngine();
        RaiseStatus();
    }

    private void RaiseEngine()
    {
        Raise(nameof(Engine));
        Raise(nameof(IsRnNoise));
        Raise(nameof(IsDeepFilterNet));
    }

    private void RaiseStatus()
    {
        Raise(nameof(Status));
        Raise(nameof(HasStatus));
        Raise(nameof(HasError));
        Raise(nameof(ShowsReport));
        Raise(nameof(IsLit));
        Raise(nameof(IsFaulty));
        Raise(nameof(ButtonName));
    }

    /// <summary>The channel left: nothing more is sent.</summary>
    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        SliderSync.Forget($"noise:{Channel}");
    }
}
