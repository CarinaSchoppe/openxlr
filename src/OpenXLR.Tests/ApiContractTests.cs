using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class ApiContractTests
{
    [Theory]
    [InlineData(null, 37890)]
    [InlineData("", 37890)]
    [InlineData("1023", 37890)]
    [InlineData("1024", 1024)]
    [InlineData("65535", 65535)]
    [InlineData("65536", 37890)]
    [InlineData("not-a-port", 37890)]
    public void ApiPortOverrideAcceptsOnlyUnprivilegedTcpPorts(string? value, int expected)
        => Assert.Equal(expected, ApiEndpoints.ResolvePort(value));

    [Fact]
    public void PublicMessagesCarryApiVersionAndCorrelatedResultStatus()
    {
        string stateJson = JsonSerializer.Serialize(new StateMessage { Connected = false }, WebSocketHub.Json);
        string successJson = JsonSerializer.Serialize(new CommandResultMessage("client-7"), WebSocketHub.Json);
        string errorJson = JsonSerializer.Serialize(new CommandResultMessage("client-8", "rejected"), WebSocketHub.Json);

        Assert.Contains("\"apiVersion\":\"1\"", stateJson);
        Assert.Contains("\"ok\":true", successJson);
        Assert.DoesNotContain("\"error\"", successJson);
        Assert.Contains("\"ok\":false", errorJson);
        Assert.Contains("\"error\":\"rejected\"", errorJson);
    }

    [Fact]
    public async Task HttpCommandReaderAcceptsACommandAndRejectsOversizeInput()
    {
        await using var valid = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"cmd\":\"setLevel\",\"channel\":\"music\",\"mix\":\"stream\",\"value\":0.5}"));
        Command? parsed = await ApiEndpoints.ReadCommandAsync(valid);

        Assert.NotNull(parsed);
        Assert.Equal("setLevel", parsed.Cmd);
        Assert.Equal("music", parsed.Channel);
        Assert.Equal(0.5, parsed.Value.GetDouble());

        await using var oversized = new MemoryStream(new byte[WebSocketHub.MaxCommandBytes + 1]);
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => ApiEndpoints.ReadCommandAsync(oversized));
        Assert.Contains("CommandTooLarge", exception.GetType().Name);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("caller-42", null)]
    [InlineData("", "requestId must not be empty")]
    [InlineData("   ", "requestId must not be empty")]
    public void RequestIdValidationIsBoundedAndUnambiguous(string? requestId, string? expected)
    {
        Assert.Equal(expected, WebSocketHub.ValidateRequestId(requestId));
        Assert.Contains("128", WebSocketHub.ValidateRequestId(new string('x', 129)));
    }

    [Fact]
    public void BundledOpenApiDocumentCoversEveryCommandFamily()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "openapi-v1.json");
        Assert.True(File.Exists(path), $"missing bundled contract: {path}");
        JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("3.1.0", document["openapi"]!.GetValue<string>());
        Assert.NotNull(document["paths"]?["/api/v1/commands"]?["post"]);
        Assert.NotNull(document["x-websocket"]?["serverMessages"]);

        JsonArray commandNodes = Assert.IsType<JsonArray>(
            document["components"]?["schemas"]?["Command"]?["properties"]?["cmd"]?["enum"]);
        var commands = commandNodes.Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        string[] required =
        [
            "getState", "getDiagnostics", "listPlugins", "listPresets", "rescanPlugins",
            "retryPlugin", "unquarantinePlugin", "set", "setLevel", "setChannelMuted",
            "setMixVolume", "setMixMuted", "createChannel", "renameChannel", "deleteChannel",
            "reorderChannels", "createMix", "renameMix", "deleteMix", "reorderMixes", "assignStream",
            "assignApp", "forgetApp", "setMonitorOutput", "setMonitorOutputs", "setOutputRoute", "setMonitoredMix",
            "setOutputVolume", "setEnforcedDefaults", "setAuxPortEnabled", "setLowCutHz",
            "setSoftClipGuard", "setInserts", "showInsertUi", "setInsertBypass", "setInsertParam",
            "retryInsertHost", "unquarantineInsert", "saveChainPreset", "loadChainPreset",
            "savePluginPreset", "loadPluginPreset", "renamePreset", "duplicatePreset", "deletePreset",
            "setActiveDevice", "saveProfile", "loadProfile", "deleteProfile",
        ];
        Assert.Empty(required.Except(commands));
        Assert.Empty(commands.Except(required));
    }
}
