using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenXLR.UI;

internal sealed class DesktopKeysWindow : Window
{
    internal DesktopKeysWindow(DesktopKeys keys, MainViewModel vm)
    {
        Title = "OpenXLR Desktop keys";
        Width = 540; Height = 540; MinWidth = 360; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");
        DesktopKeySettings saved = DesktopKeySettings.Load();
        var enabled = new CheckBox { Content = "Enable desktop integration", IsChecked = saved.Enabled };
        var choices = vm.Channels.Where(c => c.IsApplication).Select(c => (c.Id,
            Box: new CheckBox { Content = c.Name, IsChecked = saved.FocusChannels.Contains(c.Id) })).ToArray();
        var status = new TextBlock { Text = keys.Status, TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var apply = new Button { Content = "Apply and configure keys", IsDefault = true };
        var close = new Button { Content = "Close", IsCancel = true };
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        content.Children.Add(enabled);
        content.Children.Add(new TextBlock
        {
            Text = "Focused application routing uses KDE Plasma. Enable it for OpenDeck keys, then select channels below to assign PC shortcuts through the desktop's permission dialog. Keep OpenXLR running, including in the tray.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var choice in choices) content.Children.Add(choice.Box);
        content.Children.Add(status);
        content.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { apply, close } });
        Content = new ScrollViewer { Content = content };
        void Update() => Dispatcher.UIThread.Post(() => status.Text = keys.Status);
        keys.Changed += Update;
        Closed += (_, _) => keys.Changed -= Update;
        close.Click += (_, _) => Close();
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                await keys.ConfigureAsync(new DesktopKeySettings { Enabled = enabled.IsChecked == true,
                    FocusChannels = choices.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList() });
            }
            finally { apply.IsEnabled = true; }
        };
    }
}
