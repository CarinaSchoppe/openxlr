using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DiagnosticsTests
{
    [Theory]
    [InlineData("unavailable")]
    [InlineData("{}")]
    [InlineData("[{\"type\":3}]")]
    public void InvalidGraphDoesNotPreventDiagnosticsExport(string graph)
        => Assert.Contains("error", Diagnostics.SummarizeGraph(graph));

    [Fact]
    public void GraphSummaryCountsNamesRatherThanIntentionalStageDescriptions()
    {
        string graph = """
            [{"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_ch_game","node.description":"Game","media.class":"Audio/Sink"}}},
             {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_fanout_game","node.description":"Game","media.class":"Audio/Sink"}}},
             {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_ch_game","media.class":"Audio/Sink"}}},
             {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"unrelated","media.class":"Audio/Sink"}}}]
            """;
        using var result = System.Text.Json.JsonDocument.Parse(Diagnostics.SummarizeGraph(graph));
        Assert.Equal(3, result.RootElement.GetProperty("nodeCount").GetInt32());
        var duplicate = Assert.Single(result.RootElement.GetProperty("duplicates").EnumerateArray());
        Assert.Equal("OpenXLR_ch_game", duplicate.GetProperty("name").GetString());
        Assert.Equal(2, duplicate.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Redact_RemovesCommonIdentityAndSerialFields()
    {
        string input = $$"""
            home={{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}}
            host={{Environment.MachineName}}
            {"device.serial":"ABC123","object.serial":42,"application.process.id":9001}
            """;

        string redacted = Diagnostics.Redact(input);

        Assert.DoesNotContain("ABC123", redacted);
        Assert.DoesNotContain("9001", redacted);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public void Redact_StripsUsbSerialsInsidePipeWireNodeNames()
    {
        const string serial = "AAY4I55111P6X2";
        string input = $"alsa_input.usb-Elgato_Elgato_Wave_XLR_MK.2_{serial}-00.analog-stereo";

        string redacted = Diagnostics.Redact(input, [serial]);

        Assert.DoesNotContain(serial, redacted);
        Assert.Contains("alsa_input.usb-Elgato_Elgato_Wave_XLR_MK.2_<redacted>-00.analog-stereo", redacted);
    }

    [Fact]
    public void Redact_LeavesUnrelatedTokensAloneForShortSecrets()
    {
        string redacted = Diagnostics.Redact("\"clock.max-quantum\": 8192", ["/home/max"]);

        Assert.Equal("\"clock.max-quantum\": 8192", redacted);
    }

    [Fact]
    public void Redact_DoesNotTouchDigitsInsideLargerNumbers()
    {
        // A numeric USB serial must not be found inside int.MaxValue in a graph dump.
        string json = "{ \"max\": 2147483647, \"node.name\": \"alsa_card.usb-Foo_147483647-00\" }";

        string redacted = Diagnostics.Redact(json, ["147483647"]);

        Assert.Contains("\"max\": 2147483647", redacted);
        Assert.Contains("alsa_card.usb-Foo_<redacted>-00", redacted);
    }
}
