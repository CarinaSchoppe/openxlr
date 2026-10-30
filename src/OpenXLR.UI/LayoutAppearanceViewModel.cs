using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>
/// A channel's or mix's icon, colour and hidden flag, as the daemon publishes
/// them. The colour is the user's own choice for this one item, so it is a
/// brush made here rather than a skin token; without one the item keeps the
/// skin's colours.
/// </summary>
public sealed class LayoutAppearanceViewModel : ViewModelBase
{
    private string _icon = "";
    /// <summary>The stored icon: empty for the default, <c>none</c>, a legacy glyph or a name.</summary>
    public string Icon { get => _icon; private set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }

    private string _defaultIcon = "";
    /// <summary>The built-in icon the daemon chose for the item's kind; empty from a daemon that sends none.</summary>
    public string DefaultIcon { get => _defaultIcon; private set { if (Set(ref _defaultIcon, value)) Raise(nameof(HasIcon)); } }

    /// <summary>
    /// Whether the item wears an icon at all: anything but <c>none</c>, as long
    /// as there is a stored icon or a default to fall back on.
    /// </summary>
    public bool HasIcon => _icon != LayoutIcons.None && (_icon.Length > 0 || _defaultIcon.Length > 0);

    private string? _colour;
    public string? Colour => _colour;

    private IBrush? _accent;
    public IBrush? Accent => _accent;
    public bool HasColour => _accent is not null;

    /// <summary>True when the colour is light enough that dark lettering reads better on it than light.</summary>
    public bool IsLight => _accent is not null && LayoutColours.IsLight(_colour);

    private bool _hidden;
    public bool Hidden { get => _hidden; private set => Set(ref _hidden, value); }

    /// <summary>
    /// This object, so a label whose data is the appearance itself, such as a
    /// headphone output's, can use the <c>layoutAccent</c> style.
    /// </summary>
    public LayoutAppearanceViewModel Appearance => this;

    public void Apply(JsonNode? value)
    {
        string icon = value?["icon"]?.GetValue<string>() ?? "";
        Icon = LayoutIcons.IsValidChoice(icon) ? icon : "";
        string defaultIcon = value?["defaultIcon"]?.GetValue<string>() ?? "";
        DefaultIcon = LayoutIcons.IsName(defaultIcon) ? defaultIcon : "";
        Hidden = value?["hidden"]?.GetValue<bool>() ?? false;
        string? colour = value?["colour"]?.GetValue<string>();
        if (_colour == colour) return;
        _colour = colour;
        _accent = Color.TryParse(colour, out Color parsed) ? new SolidColorBrush(parsed) : null;
        Raise(nameof(Colour));
        Raise(nameof(Accent));
        Raise(nameof(HasColour));
        Raise(nameof(IsLight));
    }
}

public sealed partial class MainViewModel
{
    public Task<string?> SetLayoutAppearance(string id, bool mix, string icon, string? colour, bool hidden)
        => Edit(_client.SetLayoutAppearanceAsync(id, mix, icon, colour, hidden));

    /// <summary>The front headphone jack's icon and colour, drawn before its label in HEADPHONES.</summary>
    public LayoutAppearanceViewModel Phones1Appearance { get; } = Headset();

    /// <summary>The rear headphone jack's icon and colour.</summary>
    public LayoutAppearanceViewModel Phones2Appearance { get; } = Headset();

    private static LayoutAppearanceViewModel Headset()
    {
        var appearance = new LayoutAppearanceViewModel();
        appearance.Apply(new JsonObject { ["defaultIcon"] = "headset" });
        return appearance;
    }

    /// <summary>The appearance of a headphone output by its id, <c>phones1</c> or <c>phones2</c>.</summary>
    public LayoutAppearanceViewModel? OutputAppearance(string output) => output switch
    {
        "phones1" => Phones1Appearance,
        "phones2" => Phones2Appearance,
        _ => null,
    };

    /// <summary>Replace a headphone output's icon and colour; an output is never hidden.</summary>
    public Task<string?> SetOutputAppearance(string output, string icon, string? colour)
        => Edit(_client.SetOutputAppearanceAsync(output, icon, colour));

    private void ApplyOutputAppearance(JsonNode? outputs)
    {
        // A daemon that sends none leaves the jacks on the headset default.
        foreach ((string id, LayoutAppearanceViewModel appearance) in new[] { ("phones1", Phones1Appearance), ("phones2", Phones2Appearance) })
            appearance.Apply(outputs?[id] ?? new JsonObject { ["defaultIcon"] = "headset" });
    }

}
