using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class EffectPresetFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-preset-file-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    public EffectPresetFileTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
    private static EffectChainData Chain() => new(1, 2, JsonNode.Parse("""
        [{"id":"one","kind":"lv2","plugin":"urn:test","label":"Speech","bypass":true,"nativeHost":false,"params":{"gain":0.5}}]
        """)!.AsArray());

    [Fact]
    public async Task PortablePresetsRoundTripAndImportDoesNotChangeStoredOrLiveChains()
    {
        var original = new EffectChainPreset("Speech", Chain());
        byte[] bytes = EffectPresetFiles.Encode(original);
        await using var stream = new MemoryStream(bytes);
        var decoded = await EffectPresetFiles.DecodeAsync(stream);
        Assert.Equal(original.Name, decoded.Name);
        Assert.True(JsonNode.DeepEquals(original.Chain.Inserts, decoded.Chain.Inserts));
        decoded.Chain.Inserts[0]!["params"]!["gain"] = .9;
        Assert.Equal(.5, original.Chain.Inserts[0]!["params"]!["gain"]!.GetValue<double>());
        Assert.Empty(EffectChainPresets.Read());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"Speech\",\"chain\":null}")]
    [InlineData("{\"name\":\"Speech\",\"chain\":{\"version\":2,\"channels\":2,\"inserts\":[]}}")]
    [InlineData("{\"name\":\"\\n\",\"chain\":{\"version\":1,\"channels\":2,\"inserts\":[]}}")]
    public async Task MalformedPortablePresetsAreRefused(string text)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Exception? error = await Record.ExceptionAsync(() => EffectPresetFiles.DecodeAsync(stream));
        Assert.True(error is IOException or InvalidDataException or JsonException, error?.ToString());
        Assert.Empty(EffectChainPresets.Read());
    }

    [Fact]
    public async Task SizeLimitsAndCancellationAreEnforcedOnStreams()
    {
        await using var oversized = new MemoryStream(new byte[EffectPresetFiles.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => EffectPresetFiles.DecodeAsync(oversized));
        await using var input = new MemoryStream(EffectPresetFiles.Encode(new("Speech", Chain())));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EffectPresetFiles.DecodeAsync(input, cancelled.Token));
    }

    [Fact]
    public async Task PluginPresetsShareStorageAndRefuseOtherFormatsOrMissingTargets()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var owner = new InsertsViewModel(client, "software", 2);
        owner.Apply(new JsonArray(new JsonObject { ["insert"] = Chain().Inserts[0]!.DeepClone() }));
        var target = Assert.Single(owner.Items);
        var model = new PluginPresetViewModel(target) { Name = "Single" };
        model.Name = ""; model.Save(); Assert.NotNull(model.Error);
        model.Name = "Single"; model.Save(); Assert.Null(model.Error); Assert.Single(model.Presets);
        Assert.True(model.Fits(new("Speech", Chain())));
        var foreign = Chain(); foreign.Inserts[0]!["kind"] = "clap";
        Assert.False(model.Fits(new("Other", foreign)));
        Assert.Throws<InvalidDataException>(() => model.Import(new("Other", foreign)));
        Assert.False(await owner.ApplySinglePresetAsync(target, foreign));
        Assert.Equal("one", target.Id);
        owner.Apply(new JsonArray());
        model.Save(); Assert.Contains("no longer active", model.Error);
        Assert.False(await owner.ApplySinglePresetAsync(target, Chain()));
        Assert.Single(EffectChainPresets.Read());
    }

    [Fact]
    public async Task ApplyingOnePluginKeepsItsIdAndEveryOtherEffect()
    {
        var original = Chain(); var other = original.Inserts[0]!.DeepClone(); other!["id"] = "two";
        var received = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode? command = await SocketTestServer.Receive(socket, stop);
                if (command?["cmd"]?.GetValue<string>() != "setInserts") continue;
                received.TrySetResult(command);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start(); await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var owner = new InsertsViewModel(client, "software", 2);
        owner.Apply(new JsonArray(new JsonObject { ["insert"] = original.Inserts[0]!.DeepClone() }, new JsonObject { ["insert"] = other }));
        var changed = Chain(); changed.Inserts[0]!["id"] = "from-file"; changed.Inserts[0]!["params"]!["gain"] = .9;
        Assert.True(await owner.ApplySinglePresetAsync(owner.Items[0], changed));
        JsonNode payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("one", payload["inserts"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(.9, payload["inserts"]![0]!["params"]!["gain"]!.GetValue<double>());
        Assert.True(JsonNode.DeepEquals(other, payload["inserts"]![1]));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }
}
