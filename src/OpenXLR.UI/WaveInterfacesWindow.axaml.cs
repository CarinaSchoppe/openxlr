using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class WaveInterfacesWindow : Window
{
    public WaveInterfacesWindow() => InitializeComponent();
    public WaveInterfacesWindow(MainViewModel main) : this() => DataContext = main;
    private async void OnAddInput(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WaveInterfaceViewModel device) await device.AddInputAsync();
    }
}
