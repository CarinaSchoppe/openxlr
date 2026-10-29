using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// The Soundboard window, opened from the Sounds button on the SFX strip,
/// from the tray menu and from Options. One is open at a time: a second
/// request raises it, or reopens it under the window that asked, since an
/// owned window cannot change its owner.
/// </summary>
public partial class SoundboardWindow : Window
{
    private static SoundboardWindow? _open;

    public SoundboardWindow() => InitializeComponent();

    private SoundboardViewModel? Board => DataContext as SoundboardViewModel;

    /// <summary>The open window, for the tests.</summary>
    internal static SoundboardWindow? Current => _open;

    /// <summary>Open the window under <paramref name="owner"/>, or with no owner when the tray asks while the mixer is hidden.</summary>
    public static SoundboardWindow Open(Window? owner, SoundboardViewModel board)
    {
        if (_open is { } open)
        {
            if (open.Owner == owner && open.DataContext == board)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return open;
            }
            open.Close();
        }
        var window = new SoundboardWindow { DataContext = board };
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        _open = window;
        if (owner is { IsVisible: true }) window.Show(owner);
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }
        return window;
    }

    private void OnStopAll(object? sender, RoutedEventArgs e) => Board?.StopAll();

    private void OnTurnOn(object? sender, RoutedEventArgs e) => Board?.TurnOn();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (Board is not { CanAdd: true } board) return;
        IStorageFolder? start = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Music);
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Text("SoundboardPickFiles"),
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Text("SoundboardFileType"))
                {
                    Patterns = [.. board.Formats.Select(f => "*." + f)],
                    MimeTypes = [.. board.Formats.Select(f => f == "mp3" ? "audio/mpeg" : "audio/" + f)],
                },
                FilePickerFileTypes.All,
            ],
        });
        await board.ImportAsync([.. files.Select(LocalPath).OfType<string>()]);
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        if (Board is not { CanAdd: true } board) return;
        IStorageFolder? start = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Music);
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Text("SoundboardPickFolder"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Select(LocalPath).OfType<string>().FirstOrDefault() is { } folder) await board.ImportFolderAsync(folder);
    }

    private static string? LocalPath(IStorageItem item)
    {
        try { return item.TryGetLocalPath(); }
        catch (Exception) { return null; }
    }

    /// <summary>A pad's menu: edit, stop, move earlier or later, delete.</summary>
    private void OnPadMenu(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SoundClipViewModel clip || Board is not { } board) return;
        PadMenu(board, clip).ShowAt((Control)sender!);
    }

    /// <summary>The menu a pad's button opens, built for that clip.</summary>
    internal MenuFlyout PadMenu(SoundboardViewModel board, SoundClipViewModel clip)
    {
        var menu = new MenuFlyout();
        MenuItem Item(string name, string header, bool enabled, Action act)
        {
            var item = new MenuItem { Name = name, Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
            return item;
        }
        Item("SoundClipEdit", Localizer.Text("SoundClipEdit"), true, () => _ = EditAsync(clip));
        Item("SoundClipStop", Localizer.Text("SoundClipStop"), clip.Playing, () => board.Stop(clip));
        Item("SoundClipEarlier", Localizer.Text("SoundClipMoveEarlier"), board.CanMove(clip, -1), () => board.Move(clip, -1));
        Item("SoundClipLater", Localizer.Text("SoundClipMoveLater"), board.CanMove(clip, +1), () => board.Move(clip, +1));
        Item("SoundClipDelete", Localizer.Text("Delete"), true, () => _ = DeleteAsync(clip));
        return menu;
    }

    /// <summary>The clip's edit window, owned by this one.</summary>
    internal Task EditAsync(SoundClipViewModel clip) => new SoundClipWindow(clip).ShowDialog(this);

    private async Task DeleteAsync(SoundClipViewModel clip)
    {
        if (Board is not { } board) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("SoundClipDeleteTitle"),
                Localizer.Format("SoundClipDeleteMessage", clip.Name), Localizer.Text("Delete")))
            await board.DeleteAsync(clip);
    }
}
