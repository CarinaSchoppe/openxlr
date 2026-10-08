using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenXLR.UI;

public sealed class LayoutAppearanceViewModel : ViewModelBase
{
    public static string[] Icons { get; } = ["", "●", "♪", "♫", "✦", "◆", "▶", "◉"];
    private string _icon = "";
    public string Icon { get => _icon; private set => Set(ref _icon, value); }
    private string? _colour;
    public string? Colour => _colour;
    private IBrush? _accent;
    public IBrush? Accent => _accent;
    public bool HasColour => _accent is not null;
    private bool _hidden;
    public bool Hidden { get => _hidden; private set => Set(ref _hidden, value); }

    public void Apply(JsonNode? value)
    {
        Icon = value?["icon"]?.GetValue<string>() ?? "";
        Hidden = value?["hidden"]?.GetValue<bool>() ?? false;
        string? colour = value?["colour"]?.GetValue<string>();
        if (_colour == colour) return;
        _colour = colour;
        _accent = Color.TryParse(colour, out Color parsed) ? new SolidColorBrush(parsed) : null;
        Raise(nameof(Colour));
        Raise(nameof(Accent));
        Raise(nameof(HasColour));
    }
}

public sealed partial class MainViewModel
{
    public event Action? MiniViewChanged;
    private bool _miniView;
    public bool MiniView
    {
        get => _miniView;
        set
        {
            if (_miniView == value) return;
            if (!SavePresentationChoice(s => s with { MiniView = value })) { Reject(ref _miniView, value); return; }
            Set(ref _miniView, value);
            MiniViewChanged?.Invoke();
            Raise(nameof(ShowDetailedSections)); Raise(nameof(ShowApplications));
            RefreshChannelPresentation();
        }
    }
    public bool ShowDetailedSections => !MiniView;
    public bool ShowApplications => HasMixer && !MiniView;
    private bool _compactMixes;
    public bool CompactMixes
    {
        get => _compactMixes;
        set
        {
            if (_compactMixes == value) return;
            if (!SavePresentationChoice(s => s with { CompactMixes = value })) { Reject(ref _compactMixes, value); return; }
            Set(ref _compactMixes, value);
            RefreshChannelPresentation();
        }
    }
    public bool ShowMixSelector => CompactMixes || MiniView;
    public bool ShowChannelSelector => CompactMixer || MiniView;
    private string? _compactMixId;
    private MixViewModel? _selectedCompactMix;
    public MixViewModel? SelectedCompactMix
    {
        get => _selectedCompactMix;
        set
        {
            if (_applying || ReferenceEquals(value, _selectedCompactMix)) return;
            if (value is not null && !Mixes.Contains(value)) return;
            if (!SavePresentationChoice(s => s with { CompactMix = value?.Id }))
            {
                Dispatcher.UIThread.Post(() => Reject(ref _selectedCompactMix, value, nameof(SelectedCompactMix)));
                return;
            }
            _compactMixId = value?.Id;
            Set(ref _selectedCompactMix, value);
            RefreshChannelPresentation();
        }
    }

    private bool _compactMixer;
    public bool CompactMixer
    {
        get => _compactMixer;
        set
        {
            if (_compactMixer == value) return;
            if (!SavePresentationChoice(settings => settings with { CompactMixer = value }))
            {
                Reject(ref _compactMixer, value);
                return;
            }
            Set(ref _compactMixer, value);
            RefreshChannelPresentation();
        }
    }
    private ChannelViewModel? _selectedCompactChannel;
    private string? _compactChannelId;
    public ChannelViewModel? SelectedCompactChannel
    {
        get => _selectedCompactChannel;
        set
        {
            if (_applying || ReferenceEquals(_selectedCompactChannel, value)) return;
            if (value is not null && !Channels.Contains(value)) return;
            if (!SavePresentationChoice(settings => settings with { CompactChannel = value?.Id }))
            {
                // A selection binding finishes caching the attempted item when
                // this setter returns. Restore it afterward so it can be retried.
                Dispatcher.UIThread.Post(() => Reject(ref _selectedCompactChannel, value, nameof(SelectedCompactChannel)));
                return;
            }
            _compactChannelId = value?.Id;
            Set(ref _selectedCompactChannel, value);
            RefreshChannelPresentation();
        }
    }

    private void RefreshChannelPresentation()
    {
        var mix = Mixes.FirstOrDefault(m => m.Id == _compactMixId && m.Visible) ?? Mixes.FirstOrDefault(m => m.Visible);
        if (!ReferenceEquals(mix, _selectedCompactMix))
        {
            _selectedCompactMix = mix;
            Raise(nameof(SelectedCompactMix));
        }
        foreach (var item in Mixes) item.DisplayVisible = item.Visible && (!ShowMixSelector || ReferenceEquals(item, mix));
        foreach (var channel in Channels)
        {
            channel.ShowMiniInserts(MiniView);
            foreach (var send in channel.Sends) send.DisplayVisible = send.Visible && (!ShowMixSelector || send.MixId == mix?.Id);
        }
        Raise(nameof(ShowMixSelector)); Raise(nameof(ShowChannelSelector));
        var selected = Channels.FirstOrDefault(c => c.Id == _compactChannelId && c.Visible) ?? Channels.FirstOrDefault(c => c.Visible && !c.Appearance.Hidden)
            ?? Channels.FirstOrDefault(c => c.Visible);
        // Device changes and removed channels must not persist an automatic fallback over the user's choice.
        if (!ReferenceEquals(selected, _selectedCompactChannel))
        {
            _selectedCompactChannel = selected;
            Raise(nameof(SelectedCompactChannel));
        }
        foreach (var channel in Channels)
            channel.DisplayVisible = channel.Visible && (ShowChannelSelector ? ReferenceEquals(channel, selected) : !channel.Appearance.Hidden);
    }

    public Task<string?> SetLayoutAppearance(string id, bool mix, string icon, string? colour, bool hidden)
        => Edit(_client.SetLayoutAppearanceAsync(id, mix, icon, colour, hidden));
}
