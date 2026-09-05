using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>A compact mix-by-destination editor backed by stable route IDs.</summary>
public partial class RoutingMatrixWindow : Window
{
    private readonly MainViewModel? _vm;
    private bool _updating;

    public RoutingMatrixWindow() => InitializeComponent();

    public RoutingMatrixWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        vm.StateApplied += Rebuild;
        Opened += (_, _) => Rebuild();
        Closed += (_, _) => vm.StateApplied -= Rebuild;
    }

    private void Rebuild()
    {
        if (_vm is null || _updating) return;
        _updating = true;
        try
        {
            MatrixGrid.Children.Clear();
            MatrixGrid.RowDefinitions.Clear();
            MatrixGrid.ColumnDefinitions.Clear();
            MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160)));
            foreach (RoutingDestinationViewModel _ in _vm.RoutingDestinations)
                MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(150)));
            MatrixGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (MixViewModel _ in _vm.Mixes.Where(m => m.Visible))
                MatrixGrid.RowDefinitions.Add(new RowDefinition(new GridLength(76)));

            AddText("MIX", 0, 0, FontWeight.SemiBold, "#8b93a7");
            for (int column = 0; column < _vm.RoutingDestinations.Count; column++)
            {
                RoutingDestinationViewModel destination = _vm.RoutingDestinations[column];
                AddText(destination.Name + (destination.Available ? "" : "\n(disconnected)"),
                    0, column + 1, FontWeight.SemiBold,
                    destination.Available ? "#e6e9f0" : "#7d8496");
            }

            int row = 1;
            foreach (MixViewModel mix in _vm.Mixes.Where(m => m.Visible))
            {
                AddText(mix.Name, row, 0, FontWeight.SemiBold, "#e6e9f0");
                for (int column = 0; column < _vm.RoutingDestinations.Count; column++)
                    AddCell(mix, _vm.RoutingDestinations[column], row, column + 1);
                row++;
            }
        }
        finally { _updating = false; }
    }

    private void AddCell(MixViewModel mix, RoutingDestinationViewModel destination, int row, int column)
    {
        bool active = _vm!.HasOutputRoute(mix.Id, destination.Id);
        var toggle = new CheckBox
        {
            IsChecked = active,
            IsEnabled = destination.Available && destination.Compatible,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(toggle, destination.Error ?? $"Route {mix.Name} to {destination.Name}");
        toggle.Click += async (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            bool wanted = toggle.IsChecked == true;
            bool ok = await _vm.SetOutputRoute(mix.Id, destination.Id, wanted,
                _vm.OutputRouteStage(mix.Id, destination.Id));
            if (!ok) toggle.IsChecked = active;
            _updating = false;
        };
        Grid.SetRow(toggle, row);
        Grid.SetColumn(toggle, column);
        MatrixGrid.Children.Add(toggle);
    }

    private void AddText(string text, int row, int column, FontWeight weight, string color)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = weight,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8),
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        MatrixGrid.Children.Add(label);
    }
}
