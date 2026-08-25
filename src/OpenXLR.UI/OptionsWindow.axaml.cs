using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
    }

    public OptionsWindow(OptionsViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
