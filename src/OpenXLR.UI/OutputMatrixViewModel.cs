using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;

namespace OpenXLR.UI;

public sealed partial class MainViewModel
{
    public ObservableCollection<OutputMatrixRow> OutputMatrix { get; } = [];
    public bool HasMatrixOutputs => OutputMatrix.Count > 0;

    private void SyncOutputMatrix(JsonNode? mixer)
    {
        var selected = (mixer?["monitorOutputs"] as JsonArray)?.Select(item => item!.GetValue<string>()).ToList() ?? [];
        var groups = selected.GroupBy(OutputMatrixRow.Key).ToList();
        var feeds = mixer?["monitorFeeds"] as JsonObject;
        var levels = new Dictionary<(string Output, string Mix), double>();
        if (mixer?["outputRoutes"] is JsonArray routes)
            foreach (JsonNode? route in routes)
                if (route is not null)
                    levels[(OutputMatrixRow.Key(route["device"]!.GetValue<string>()), route["mix"]!.GetValue<string>())]
                        = route["level"]!.GetValue<double>();
        int position = 0;
        foreach (var group in groups)
        {
            OutputMatrixRow? row = OutputMatrix.FirstOrDefault(row => row.Id == group.Key);
            if (row is null)
            {
                row = new OutputMatrixRow(group.Key, _client);
                OutputMatrix.Insert(position, row);
            }
            else if (OutputMatrix.IndexOf(row) != position) OutputMatrix.Move(OutputMatrix.IndexOf(row), position);
            row.Label = string.Join(" + ", group.Select(name => MonitorOutputs.FirstOrDefault(output => output.Name == name)?.Label ?? name));
            string feed = feeds?[group.First()]?.GetValue<string>() ?? Mixes.FirstOrDefault(mix => mix.IsMonitor)?.Id ?? "monitor";
            var included = feed.Split('+', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            row.Sync(Mixes, mix => included.Contains(mix) ? levels.GetValueOrDefault((group.Key, mix), 1) : 0);
            position++;
        }
        while (OutputMatrix.Count > position)
        {
            foreach (OutputRouteViewModel route in OutputMatrix[^1].Routes) route.Detach();
            OutputMatrix.RemoveAt(OutputMatrix.Count - 1);
        }
        Raise(nameof(HasMatrixOutputs));
    }
}

public sealed class OutputMatrixRow(string id, DaemonClient client) : ViewModelBase, IHasId
{
    public string Id { get; } = id;
    private string _label = id;
    public string Label { get => _label; set => Set(ref _label, value); }
    public ObservableCollection<OutputRouteViewModel> Routes { get; } = [];

    internal static string Key(string name)
    {
        int marker = name.IndexOf('#');
        return marker < 0 ? name : name[..marker] + "#bus";
    }

    internal void Sync(IEnumerable<MixViewModel> mixes, Func<string, double> level)
    {
        int position = 0;
        foreach (MixViewModel mix in mixes)
        {
            OutputRouteViewModel? route = Routes.FirstOrDefault(route => route.Id == mix.Id);
            if (route is null)
            {
                route = new OutputRouteViewModel(client, Id, mix.Id);
                Routes.Insert(position, route);
            }
            else if (Routes.IndexOf(route) != position) Routes.Move(Routes.IndexOf(route), position);
            route.Sync(mix.Name, level(mix.Id));
            position++;
        }
        while (Routes.Count > position)
        {
            Routes[^1].Detach();
            Routes.RemoveAt(Routes.Count - 1);
        }
    }
}

public sealed class OutputRouteViewModel(DaemonClient client, string output, string mix) : ViewModelBase, IHasId
{
    public string Id { get; } = mix;
    private string _name = mix;
    public string Name { get => _name; private set => Set(ref _name, value); }
    private double _level;
    private bool _syncing;
    private bool _detached;
    private readonly string _sliderKey = $"route:{output}:{mix}";
    public double Level
    {
        get => _level;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Round(Math.Clamp(value, 0, 1) * 100) / 100;
            if (!Set(ref _level, value)) return;
            Raise(nameof(LevelText));
            if (_syncing || _detached) return;
            SliderSync.Touch(_sliderKey);
            double level = value;
            SliderSync.Send(_sliderKey, () => _ = client.SetOutputRouteAsync(output, Id, level));
        }
    }
    public string LevelText => Level == 0 ? "Off" : $"{Level * 100:0}%";
    internal void Detach()
    {
        _detached = true;
        SliderSync.Forget(_sliderKey);
    }
    internal void Sync(string name, double level)
    {
        _syncing = true;
        try
        {
            Name = name;
            if (!SliderSync.RecentlyTouched(_sliderKey)) Level = level;
        }
        finally { _syncing = false; }
    }
}
