using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class SpaStringTests
{
    [Theory]
    [InlineData("http://lsp-plug.in/plugins/lv2/compressor_mono", "http://lsp-plug.in/plugins/lv2/compressor_mono")]
    [InlineData("http://gareus.org/oss/lv2/darc#mono", "http://gareus.org/oss/lv2/darc\\u0023mono")]
    [InlineData("OpenXLR Mic #2 Inserts", "OpenXLR Mic \\u0023" + "2 Inserts")]
    [InlineData("say \"hi\" \\ back", "say \\\"hi\\\" \\\\ back")]
    public void HashesQuotesAndBackslashesSurviveThePropertyParser(string input, string expected)
        => Assert.Equal(expected, PipeWireAdapter.SpaString(input));

    // The module-argument string pactl hands over is parsed twice, and each
    // pass strips one layer of escapes: the value is escaped for the inner
    // pass, the list around it for the outer. This exact string was loaded
    // on PipeWire 1.6.8 and read back as the name with the properties after
    // it intact.
    [Fact]
    public void AModuleArgumentValueIsEscapedOnceForEachParsingPass()
    {
        string value = PipeWireAdapter.PropValue("a\"b'c\\d e");
        Assert.Equal("'a\"b\\'c\\\\d e'", value);
        Assert.Equal("\"node.description='a\\\"b\\\\'c\\\\\\\\d e' priority.session=90\"",
            PipeWireAdapter.PropList($"node.description={value} priority.session=90"));
    }
}
