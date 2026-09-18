using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class OutputMatrixWindow : Window
{
    public OutputMatrixWindow() => InitializeComponent();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
