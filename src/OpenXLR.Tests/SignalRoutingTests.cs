using OpenXLR.Core.Mixing;
using System.Text.Json;

namespace OpenXLR.Tests;

public sealed class SignalRoutingTests
{
    private static readonly RoutingDestination Speakers = new(
        "output:speakers", "Speakers", "alsa_output.card", true, true, true,
        [ProcessingStage.MixProcessed, ProcessingStage.FullFx]);

    [Fact]
    public void StableDestinationIdIsDeterministicAndOpaque()
    {
        string first = SignalRouting.StableDestinationId("serial=abc|port=hp1");
        string second = SignalRouting.StableDestinationId("serial=abc|port=hp1");

        Assert.Equal(first, second);
        Assert.StartsWith("output:", first);
        Assert.DoesNotContain("abc", first);
        Assert.NotEqual(first, SignalRouting.StableDestinationId("serial=abc|port=hp2"));
    }

    [Fact]
    public void PipeWireObjectSerialDoesNotAffectStableHardwareIdentity()
    {
        using JsonDocument first = JsonDocument.Parse(
            """{"device.serial":"hardware-42","api.alsa.path":"usb-1","object.serial":100}""");
        using JsonDocument second = JsonDocument.Parse(
            """{"device.serial":"hardware-42","api.alsa.path":"usb-1","object.serial":999}""");

        string a = PipeWireAdapter.StableAudioNodeId(first.RootElement, "old-node", "sink");
        string b = PipeWireAdapter.StableAudioNodeId(second.RootElement, "new-node", "sink");

        Assert.Equal(a, b);
    }

    [Fact]
    public void ValidRoutePasses()
    {
        var route = new OutputRouteDefinition("stream", Speakers.Id);

        Assert.Null(SignalRouting.Validate(route, ["stream"], [Speakers]));
    }

    [Fact]
    public void DisconnectedDestinationCannotBeNewlyApplied()
    {
        RoutingDestination disconnected = Speakers with { Available = false, NodeName = null };

        string? error = SignalRouting.Validate(
            new OutputRouteDefinition("stream", disconnected.Id), ["stream"], [disconnected]);

        Assert.Contains("disconnected", error);
    }

    [Fact]
    public void UnsupportedProcessingStageIsRejected()
    {
        string? error = SignalRouting.Validate(
            new OutputRouteDefinition("stream", Speakers.Id, ProcessingStage.Raw),
            ["stream"], [Speakers]);

        Assert.Contains("unavailable", error);
    }

    [Fact]
    public void DirectAndIndirectFeedbackAreRejected()
    {
        var target = new RoutingDestination("mix:stream", "Stream", "internal", true, false,
            true, [ProcessingStage.MixProcessed]);
        Assert.Contains("itself", SignalRouting.Validate(
            new OutputRouteDefinition("stream", target.Id), ["stream"], [target]));

        Assert.True(SignalRouting.WouldCreateCycle("mix:a", "mix:b",
            [("mix:b", "mix:c"), ("mix:c", "mix:a")]));
        Assert.False(SignalRouting.WouldCreateCycle("mix:a", "output:speakers",
            [("mix:b", "output:headphones")]));
    }

    [Fact]
    public void RouteStateRoundTripsAsStrings()
    {
        var original = new MixerSettings
        {
            OutputRoutes = [new("stream", Speakers.Id, ProcessingStage.FullFx, "Studio")],
        };
        string directory = Directory.CreateTempSubdirectory("openxlr-routing-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        try
        {
            original.Save(path);
            string json = File.ReadAllText(path);
            MixerSettings restored = MixerSettings.Load(path)!;

            Assert.Contains("\"stage\": \"FullFx\"", json);
            Assert.Equal(original.OutputRoutes, restored.OutputRoutes);
            Assert.Equal(MixerSettings.CurrentSchemaVersion, restored.SchemaVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConvergingRoutesAreAlignedToSlowestSource()
    {
        IReadOnlyDictionary<string, int> plan = LatencyCompensation.Calculate(
            [("dry", 0), ("compressor", 64), ("lookahead", 512)]);

        Assert.Equal(512, plan["dry"]);
        Assert.Equal(448, plan["compressor"]);
        Assert.Equal(0, plan["lookahead"]);
    }

    [Fact]
    public void LatencyPlanClampsInvalidAndExcessiveReports()
    {
        IReadOnlyDictionary<string, int> plan = LatencyCompensation.Calculate(
            [("negative", -10), ("excessive", int.MaxValue)]);

        Assert.Equal(CompensationDelayHost.MaximumSamples, plan["negative"]);
        Assert.Equal(0, plan["excessive"]);
    }

    [Fact]
    public void PipeWireQuantumParserIgnoresInactiveOverride()
    {
        const string metadata = "update: id:0 key:'clock.quantum' value:'256' type:''\n" +
            "update: id:0 key:'clock.force-quantum' value:'0' type:''\n";

        Assert.Equal(256, PipeWireAdapter.ParseGraphQuantum(metadata));
    }
}
