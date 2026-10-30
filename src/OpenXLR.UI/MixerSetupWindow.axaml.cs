using OpenXLR.UI.Localization;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>
/// The layout editor: add, rename, reorder and delete application channels
/// and user mixes. Every action waits for the daemon's answer, which
/// arrives after the new layout is saved; the lists update from the state
/// push that precedes it, so nothing here is optimistic.
/// </summary>
public partial class MixerSetupWindow : Window
{
    public MixerSetupWindow()
    {
        InitializeComponent();
        LayoutIconPicker.Fill(NewChannelIcon, "", LayoutDefaultIcons.ForChannel("", false, null));
        LayoutIconPicker.Fill(NewMixIcon, "", NewMixDefaultIcon);
        NewMixKind.SelectionChanged += (_, _) => LayoutIconPicker.SetDefault(NewMixIcon, NewMixDefaultIcon);
    }

    private string NewMixKindName => NewMixKind.SelectedIndex == 1 ? "monitor" : "virtualMic";

    /// <summary>What a mix of the chosen kind wears by default; a new mix never has a structural id.</summary>
    private string NewMixDefaultIcon => LayoutDefaultIcons.ForMix("", NewMixKindName);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnExclusiveGroups(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await new ExclusiveGroupsWindow(vm).ShowDialog(this);
    }

    private void OnDucking(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) DuckingWindow.Open(this, vm.Ducking);
    }

    private async void OnAddChannel(object? sender, RoutedEventArgs e)
    {
        string name = ChannelName.Text?.Trim() ?? "";
        if (name.Length == 0 || Vm is not { } vm) return;
        if (await Run(vm.CreateChannel(name, LayoutIconPicker.Chosen(NewChannelIcon))))
        {
            ChannelName.Text = "";
            LayoutIconPicker.Fill(NewChannelIcon, "", LayoutDefaultIcons.ForChannel("", false, null));
        }
    }

    private async void OnAddCapture(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var name = new TextBox { Name = "CaptureName", MaxLength = 60, PlaceholderText = Localizer.Text("ChannelNamePlaceholder") };
        // Each source by its label, its node name in the tooltip, as the source dialog shows them.
        var source = new ComboBox { Name = "CaptureSource", PlaceholderText = Localizer.Text("CaptureSourcePlaceholder"), ItemsSource = vm.Inputs.Where(d => !d.IsOwn).ToArray(),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AudioDeviceItem>((device, _) =>
            {
                var text = new TextBlock { Text = device?.Label, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(text, device?.Name);
                return text;
            }) };
        ComboBox port = InputBindingDialog.PortPicker("CapturePort");
        AutomationProperties.SetName(port, Localizer.Text("InputBindingPort"));
        source.SelectionChanged += (_, _) =>
        {
            AudioDeviceItem? device = source.SelectedItem as AudioDeviceItem;
            InputBindingDialog.FillCapturePorts(port, device, mono: device?.Channels == 1 ? 0 : null);
        };
        var add = new Button { Name = "CreateCapture", Content = Localizer.Text("AddInput"), IsDefault = true };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var dialog = new Window
        {
            Title = Localizer.Text("AddCaptureInput"), Width = 480, Height = 340, MinWidth = 360, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "dialog" },
            Content = new ScrollViewer { Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 12,
                Children = { name, source, port,
                    new TextBlock { Text = Localizer.Text("AddCaptureInputHint"), TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { cancel, add } } },
            } },
        };
        bool accepted = false;
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || source.SelectedItem is not AudioDeviceItem || port.SelectedItem is not InputBindingDialog.PortChoice) return;
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        if (accepted && source.SelectedItem is AudioDeviceItem selected && port.SelectedItem is InputBindingDialog.PortChoice chosen)
            await Run(vm.CreateCaptureChannel(name.Text!.Trim(), selected.Name, chosen.Pair, chosen.MonoChannel));
    }

    private async void OnAddMix(object? sender, RoutedEventArgs e)
    {
        string name = MixName.Text?.Trim() ?? "";
        if (name.Length == 0 || Vm is not { } vm) return;
        if (await Run(vm.CreateMix(name, NewMixKindName, LayoutIconPicker.Chosen(NewMixIcon))))
        {
            MixName.Text = "";
            LayoutIconPicker.Fill(NewMixIcon, "", NewMixDefaultIcon);
        }
    }

    private void OnChannelNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnAddChannel(sender, e); e.Handled = true; }
    }

    private void OnMixNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnAddMix(sender, e); e.Handled = true; }
    }

    private async void OnChannelUp(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is { } c && Vm is { } vm) await Run(vm.MoveChannel(c.Id, -1));
    }

    private async void OnChannelDown(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is { } c && Vm is { } vm) await Run(vm.MoveChannel(c.Id, +1));
    }

    private async void OnMixUp(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is { } m && Vm is { } vm) await Run(vm.MoveMix(m.Id, -1));
    }

    private async void OnMixDown(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is { } m && Vm is { } vm) await Run(vm.MoveMix(m.Id, +1));
    }

    private async void OnRenameChannel(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is not { } channel || Vm is not { } vm) return;
        string? name = await PromptName(Localizer.Format("RenameChannelTitle", channel.Name), channel.Name,
            channel.IsApplication ? Localizer.Text("RenameAppChannelHint")
                : Localizer.Text("RenameCaptureChannelHint"));
        if (name is not null && name != channel.Name) await Run(vm.RenameChannel(channel.Id, name));
    }

    private async void OnRenameMix(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is not { } mix || Vm is not { } vm) return;
        string? name = await PromptName(Localizer.Format("RenameMixTitle", mix.Name), mix.Name,
            Localizer.Text("RenameMixHint"));
        if (name is not null && name != mix.Name) await Run(vm.RenameMix(mix.Id, name));
    }

    private async void OnDeleteChannel(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is not { } channel || Vm is not { } vm) return;
        if (await Confirm(Localizer.Format("DeleteChannelTitle", channel.Name),
                channel.IsApplication ? Localizer.Format("DeleteAppChannelDetail", FallbackAfterDeleting(vm, channel))
                    : Localizer.Text("DeleteCaptureChannelDetail")))
            await Run(vm.DeleteChannel(channel.Id));
    }

    private async void OnDeleteMix(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is not { } mix || Vm is not { } vm) return;
        if (await Confirm(Localizer.Format("DeleteMixTitle", mix.Name),
                mix.IsMonitor
                    ? Localizer.Text("DeleteMonitorMixDetail")
                    : Localizer.Text("DeleteVirtualMicDetail")))
            await Run(vm.DeleteMix(mix.Id));
    }

    /// <summary>The channel the daemon moves a deleted channel's programs to: System, else the first by id.</summary>
    private static string FallbackAfterDeleting(MainViewModel vm, ChannelViewModel deleted)
    {
        var rest = vm.Channels.Where(c => c.IsApplication && c.Id != deleted.Id).ToList();
        ChannelViewModel? fallback = rest.FirstOrDefault(c => c.Id == "system") ?? rest.MinBy(c => c.Id, StringComparer.Ordinal);
        return fallback?.Name ?? Localizer.Text("AnotherApplicationChannel");
    }

    private async void OnAppearance(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        ChannelViewModel? channel = Item<ChannelViewModel>(sender);
        MixViewModel? mix = Item<MixViewModel>(sender);
        // The headphone outputs' buttons carry the output id; they have an icon and a colour, never a hidden flag.
        string? output = channel is null && mix is null && (sender as Control)?.Tag is "phones1" or "phones2"
            ? (string)((Control)sender!).Tag! : null;
        LayoutAppearanceViewModel? appearance = channel?.Appearance ?? mix?.Appearance ?? (output is null ? null : vm.OutputAppearance(output));
        if (appearance is null) return;
        string name = channel?.Name ?? mix?.Name ?? (output == "phones1" ? Localizer.Text("Phones1") : Localizer.Text("Phones2"));
        var icon = new ComboBox { Name = "AppearanceIcon", MinWidth = 200, MaxDropDownHeight = 360 };
        AutomationProperties.SetName(icon, Localizer.Text("Icon"));
        LayoutIconPicker.Fill(icon, appearance.Icon, appearance.DefaultIcon.Length > 0 ? appearance.DefaultIcon
            : channel is not null ? LayoutDefaultIcons.ForChannel(channel.Id, channel.IsHardware, channel.CaptureSource)
            : mix is not null ? LayoutDefaultIcons.ForMix(mix.Id, mix.Kind) : "headset");
        var colour = new TextBox { Name = "AppearanceColour", Text = appearance.Colour ?? "", MaxLength = 7,
            PlaceholderText = Localizer.Text("ColourPlaceholder") };
        AutomationProperties.SetName(colour, Localizer.Text("Colour"));
        // The palette fills the field; the field still takes a colour of the user's own.
        var swatches = new WrapPanel { Name = "AppearanceSwatches", ItemSpacing = 2, LineSpacing = 2 };
        ColourSwatches.Fill(swatches, colour);
        // A channel or a mix can be hidden, under the same rule; a headphone output is hardware and always shows.
        var hidden = new CheckBox { Name = "AppearanceHidden", IsChecked = appearance.Hidden, IsVisible = output is null,
            Content = mix is not null ? Localizer.Text("HideMix") : Localizer.Text("HideChannelStrip") };
        var save = new Button { Name = "AppearanceSave", Content = Localizer.Text("Save"), IsDefault = true };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var dialog = new Window
        {
            Title = Localizer.Format("AppearanceOfTitle", name), Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Localizer.Text("Icon") }, icon,
                    new TextBlock { Text = Localizer.Text("Colour") }, swatches, colour,
                    hidden,
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "hint" },
                        Text = Localizer.Text("AppearanceDialogHint") },
                    note,
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, save } },
                },
            },
        };
        save.Click += async (_, _) =>
        {
            string? chosen = string.IsNullOrWhiteSpace(colour.Text) ? null : colour.Text.Trim();
            if (chosen is not null && !LayoutColours.IsHex(chosen))
            {
                note.Text = Localizer.Text("ColourFormatHint");
                return;
            }
            save.IsEnabled = false;
            try
            {
                string? error = output is not null
                    ? await vm.SetOutputAppearance(output, LayoutIconPicker.Chosen(icon), chosen)
                    : await vm.SetLayoutAppearance(channel?.Id ?? mix!.Id, mix is not null,
                        LayoutIconPicker.Chosen(icon), chosen, hidden.IsChecked == true);
                if (error is null) dialog.Close(); else note.Text = error;
            }
            finally { save.IsEnabled = true; }
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private static T? Item<T>(object? sender) where T : class => (sender as Control)?.DataContext as T;

    /// <summary>Run one edit with the window locked, and surface its error here as well as in the status line.</summary>
    private async Task<bool> Run(Task<string?> edit)
    {
        IsEnabled = false;
        try
        {
            string? error = await edit;
            Note.Text = error ?? "";
            Note.IsVisible = error is not null;
            return error is null;
        }
        finally { IsEnabled = true; }
    }

    private Task<string?> PromptName(string title, string current, string hint)
        => Dialogs.NameAsync(this, title, current, hint, 60);

    private async Task<bool> Confirm(string title, string message)
    {
        var yes = new Button { Content = Localizer.Text("Delete"), Classes = { "danger" } };
        var no = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var done = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { no, yes },
                    },
                },
            },
        };
        yes.Click += (_, _) => { done.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { done.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => done.TrySetResult(false);
        await dialog.ShowDialog(this);
        return await done.Task;
    }

    private const string FileLimitManual =
        "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#open-files";

    private void OnFileLimitManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(FileLimitManual);

    private async void OnRestartDaemon(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.DaemonRestart.RestartAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
