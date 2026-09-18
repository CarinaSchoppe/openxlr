using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class OutputMatrixTests
{
    [Fact]
    public async Task UiPreservesLiveControlsAndGroupsSharedJacksWithoutEchoingUpdates()
    {
        await using var client = new DaemonClient();
        var vm = new MainViewModel(client);
        vm.Mixes.Add(new MixViewModel(client, "monitor", "Monitor A"));
        vm.Mixes.Add(new MixViewModel(client, "stream", "Stream") { Kind = "virtualMic" });
        var state = JsonNode.Parse("""
            {"monitorOutputs":["pro#hp1","pro#hp2","speakers"],
             "monitorFeeds":{"pro#hp1":"monitor+stream","pro#hp2":"monitor+stream","speakers":""},
             "outputRoutes":[{"device":"pro#bus","mix":"stream","level":0.4}]}
            """);
        MethodInfo sync = typeof(MainViewModel).GetMethod("SyncOutputMatrix", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sync.Invoke(vm, [state]);
        Assert.Equal(2, vm.OutputMatrix.Count);
        Assert.Equal("pro#bus", vm.OutputMatrix[0].Id);
        Assert.Equal([1.0, .4], vm.OutputMatrix[0].Routes.Select(route => route.Level));
        Assert.All(vm.OutputMatrix[1].Routes, route => Assert.Equal(0, route.Level));
        OutputRouteViewModel control = vm.OutputMatrix[0].Routes[1];
        Assert.False(SliderSync.RecentlyTouched("route:pro#bus:stream"));
        vm.Mixes.Move(1, 0);
        vm.Mixes[0].Name = "Recording";
        sync.Invoke(vm, [state]);
        Assert.Same(control, vm.OutputMatrix[0].Routes[0]);
        Assert.Equal("Recording", control.Name);
        control.Level = .3;
        Assert.True(SliderSync.RecentlyTouched("route:pro#bus:stream"));
        vm.Mixes.RemoveAt(0);
        state!["monitorOutputs"] = new JsonArray("speakers");
        sync.Invoke(vm, [state]);
        Assert.Equal("speakers", Assert.Single(vm.OutputMatrix).Id);
        Assert.Single(vm.OutputMatrix[0].Routes);
        Assert.False(SliderSync.RecentlyTouched("route:pro#bus:stream"));
        var picker = new MonitorOutputItem("speakers", "Speakers", () => { }, (_, _) => Assert.Fail("state echoed as command"));
        picker.SyncFeed([new("monitor", "Monitor A")], "");
        Assert.Equal("Silent", picker.Feed!.Name);
    }

    [Fact]
    public void SavedRouteGainsAndExplicitSilenceRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-matrix-" + Guid.NewGuid());
        string path = Path.Combine(directory, "mixer.json");
        try
        {
            var settings = new MixerSettings
            {
                MonitorOutputs = ["headset", "speakers"],
                MonitorFeeds = new() { ["headset"] = "monitor+chat", ["speakers"] = "" },
                OutputRoutes = [new("headset", "chat", .45)],
            };
            Assert.Null(settings.Save(path));
            MixerSettings loaded = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.Equal(settings.OutputRoutes, loaded.OutputRoutes);
            Assert.Equal("", loaded.MonitorFeeds["speakers"]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidSavedRoutesAreDroppedButAProfileIsRefusedAsAWhole()
    {
        OutputRouteLevel[] invalid = [null!, new("device", "monitor", double.NaN), new("", "monitor", .5),
            new("device", "monitor", 0), new("device", "monitor", 2), new(new string('x', 257), "monitor", .5)];
        foreach (OutputRouteLevel route in invalid)
        {
            MixerSettings safe = SavedMixerValidation.Sanitize(new MixerSettings
                { OutputRoutes = [new("headset", "stream", .5), route] }, out var dropped);
            Assert.Equal(.5, Assert.Single(safe.OutputRoutes).Level);
            Assert.NotEmpty(dropped);
            Assert.Throws<System.Text.Json.JsonException>(() => SavedMixerValidation.Validate(new MixerScene { OutputRoutes = [route] }));
        }
        var many = Enumerable.Range(0, 305).Select(i => new OutputRouteLevel($"out{i}", "monitor", .5)).ToList();
        Assert.Equal(304, SavedMixerValidation.Sanitize(new MixerSettings { OutputRoutes = many }, out _).OutputRoutes.Count);
        Assert.Throws<System.Text.Json.JsonException>(() => SavedMixerValidation.Validate(new MixerScene { OutputRoutes = many }));
    }
}
