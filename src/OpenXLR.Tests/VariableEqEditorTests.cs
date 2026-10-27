using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using Eq = OpenXLR.UI.MicEq;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class VariableEqEditorTests
{
    private sealed class Commands : IEqCommands
    {
        public List<Eq> Whole { get; } = [];
        public List<int> Partial { get; } = [];
        public Task SetEqAsync(string channel, Eq eq) { Whole.Add(eq); return Task.CompletedTask; }
        public Task SetEqEnabledAsync(string channel, bool value) => Task.CompletedTask;
        public Task SetEqProcessingAsync(string channel, string value) => Task.CompletedTask;
        public Task SetEqBandAsync(string channel, int band, EqBandChange change) { Partial.Add(band); return Task.CompletedTask; }
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static JsonNode Node(Eq eq) => JsonSerializer.SerializeToNode(eq, Web)!;
    private static JsonNode Limits => JsonSerializer.SerializeToNode(EqLimits.Instance, Web)!;

    [Fact]
    public void AddRemoveAndResetKeepBoundedBandsAndDiscardFormerBandWrites()
    {
        var commands = new Commands();
        using var model = new MicEqViewModel(commands, "xlr1", "Mic", TimeSpan.Zero);
        model.Apply(Node(Eq.Default), Limits);
        while (model.CanAddBand) model.AddBand();
        Assert.Equal(16, model.Bands.Count);
        Assert.Equal(12, commands.Whole.Count);
        Assert.All(model.Bands.Skip(4), band => Assert.Equal(0, band.Gain));
        var removed = model.Bands[1];
        model.RemoveBand(removed);
        Assert.Equal(15, model.Bands.Count);
        removed.Gain = 3;
        Assert.Empty(commands.Partial);
        while (model.CanRemoveBand) model.RemoveBand(model.Bands[^1]);
        Assert.Single(model.Bands);
        model.Reset();
        Assert.Equal(4, model.Bands.Count);
        Assert.Equal(Eq.Default, model.Current);
    }

    [Fact]
    public void LateShapeEchoesCannotUndoRepeatedAddsButAnExternalChangeEventuallyWins()
    {
        var clock = new Clock(); var commands = new Commands();
        using var model = new MicEqViewModel(commands, "xlr1", "Mic", TimeSpan.Zero, clock);
        model.Apply(Node(Eq.Default), Limits);
        model.AddBand(); model.AddBand();
        Eq local = model.Current;
        model.Apply(Node(local), Limits);
        model.Apply(Node(Eq.Default), Limits);
        Assert.Equal(6, model.Bands.Count);
        clock.Ticks += TimeSpan.FromSeconds(2).Ticks;
        model.Apply(Node(Eq.Default), Limits);
        Assert.Equal(4, model.Bands.Count);
    }

    [Fact]
    public void LegacyMetadataDoesNotOfferStructuralCommands()
    {
        var commands = new Commands();
        using var model = new MicEqViewModel(commands, "xlr1", "Mic", TimeSpan.Zero);
        model.Apply(Node(Eq.Default), Limits);
        model.Apply(Node(Eq.Default), JsonNode.Parse("{\"bands\":4}"));
        Assert.False(model.VariableBands);
        model.AddBand(); model.RemoveBand(model.Bands[0]);
        Assert.Empty(commands.Whole);
    }

    [Fact]
    public void MissingMetadataAfterAReconnectRestoresLegacyBounds()
    {
        using var model = new MicEqViewModel(new Commands(), "xlr1", "Mic");
        model.Apply(Node(Eq.Default), Limits);
        Assert.True(model.VariableBands);
        model.Apply(Node(Eq.Default), null);
        Assert.False(model.VariableBands);
        Assert.False(model.CanAddBand);
        Assert.False(model.CanRemoveBand);
    }
}
