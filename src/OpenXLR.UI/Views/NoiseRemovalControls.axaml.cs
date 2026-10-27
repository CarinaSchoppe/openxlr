using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI.Views;

/// <summary>A microphone's noise removal controls. Its data context is a <see cref="MicNoiseViewModel"/>.</summary>
public partial class NoiseRemovalControls : UserControl
{
    public NoiseRemovalControls()
    {
        InitializeComponent();
    }

    // Two sides of one choice: the lit side is the daemon's, a press asks for its side.
    private void OnRnNoise(object? sender, RoutedEventArgs e) => (DataContext as MicNoiseViewModel)?.ChooseEngine(MicNoise.RnNoise);
    private void OnDeepFilterNet(object? sender, RoutedEventArgs e) => (DataContext as MicNoiseViewModel)?.ChooseEngine(MicNoise.DeepFilterNet);
}
