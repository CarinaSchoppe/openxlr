using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class BlockSizeViewModelTests
{
    private static void Report(MainViewModel model, JsonObject? mixer)
        => typeof(MainViewModel).GetMethod("ApplyBlockSize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [mixer]);

    [Fact]
    public async Task TheChoiceAppearsOnlyWhenTheDaemonOffersSizes()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Assert.False(model.BlockSizeReported);
        Report(model, new JsonObject());
        Assert.False(model.BlockSizeReported);
        Report(model, new JsonObject { ["blockSizes"] = new JsonArray(256, 512, 1024) });
        Assert.True(model.BlockSizeReported);
        Assert.Equal(4, model.BlockSizeChoices.Count);
        Assert.Equal(0, model.BlockSizeIndex);
        Report(model, new JsonObject { ["blockSizes"] = new JsonArray(256, 512, 1024), ["blockSize"] = 512 });
        Assert.Equal(2, model.BlockSizeIndex);
        Report(model, null);
        Assert.False(model.BlockSizeReported);
        Assert.Equal(0, model.BlockSizeIndex);
    }

    [Fact]
    public async Task AnItemResetLeavesTheChoiceAlone()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Report(model, new JsonObject { ["blockSizes"] = new JsonArray(256, 512, 1024), ["blockSize"] = 256 });
        model.BlockSizeIndex = -1;
        model.BlockSizeIndex = 9;
        Assert.Equal(1, model.BlockSizeIndex);
    }

    [Fact]
    public void LabelsGiveSamplesAndMilliseconds()
        => Assert.Contains("256", MainViewModel.BlockSizeLabel(256));
}
