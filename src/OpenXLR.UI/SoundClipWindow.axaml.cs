using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>A clip's edit window, opened from its pad's menu in the Soundboard window.</summary>
public partial class SoundClipWindow : Window
{
    public SoundClipWindow() => InitializeComponent();

    public SoundClipWindow(SoundClipViewModel clip) : this()
    {
        var edit = new SoundClipEditViewModel(clip);
        DataContext = edit;
        // The icon picker is the layout editor's, which draws each entry; a clip's default is SFX's.
        LayoutIconPicker.Fill(SoundClipIcon, clip.Appearance.Icon, SoundClipViewModel.DefaultIcon);
        SoundClipIcon.SelectionChanged += (_, _) => edit.Icon = LayoutIconPicker.Chosen(SoundClipIcon);
        Opened += (_, _) => { SoundClipName.Focus(); SoundClipName.SelectAll(); };
    }

    internal SoundClipEditViewModel? Edit => DataContext as SoundClipEditViewModel;

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Edit is not { } edit) return;
        SoundClipSave.IsEnabled = false;
        try
        {
            if (await edit.SaveAsync()) Close();
        }
        finally { SoundClipSave.IsEnabled = true; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
