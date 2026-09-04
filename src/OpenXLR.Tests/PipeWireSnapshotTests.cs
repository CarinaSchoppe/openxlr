using System.Text;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PipeWireSnapshotTests
{
    [Fact]
    public void SingleArrayIsPreserved()
    {
        const string input = """[{"id":1,"info":{"props":{"node.name":"music"}}}]""";
        using JsonDocument result = PipeWireSnapshot.Parse(Encoding.UTF8.GetBytes(input));
        Assert.Equal(input, result.RootElement.GetRawText());
    }

    [Fact]
    public void BatchesReplaceByIdAndRemoveBothTombstoneShapes()
    {
        using JsonDocument result = PipeWireSnapshot.Parse("""
            [{"id":1,"info":{"state":"idle"}},{"id":2},{"id":3}]
            [{"id":1,"info":{"state":"running"}},{"id":2,"info":null}]
            [{"id":3,"props":null},{"id":4,"info":{"state":"idle"}}]
            """u8);
        JsonElement[] objects = result.RootElement.EnumerateArray().ToArray();
        Assert.Equal([1, 4], objects.Select(o => o.GetProperty("id").GetInt32()));
        Assert.Equal("running", objects[0].GetProperty("info").GetProperty("state").GetString());
    }

    [Fact]
    public void RemovedIdCanBeReusedAndUnknownRemovalIsHarmless()
    {
        using JsonDocument result = PipeWireSnapshot.Parse("""
            [{"id":1}] [{"id":1,"info":null},{"id":9,"info":null}] [{"id":1,"type":"new"}]
            """u8);
        Assert.Equal("new", Assert.Single(result.RootElement.EnumerateArray()).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[] {}")]
    [InlineData("[] [")]
    [InlineData("[] [null]")]
    [InlineData("[] [{}]")]
    [InlineData("[] [{\"id\":-1}]")]
    [InlineData("[] [{\"id\":\"2\"}]")]
    [InlineData("[] [{\"id\":4294967296}]")]
    public void InvalidBatchesAreRejected(string input)
        => Assert.ThrowsAny<JsonException>(() => PipeWireSnapshot.Parse(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public void EmptyBatchesAreValid()
    {
        using JsonDocument result = PipeWireSnapshot.Parse("[] []"u8);
        Assert.Empty(result.RootElement.EnumerateArray());
    }
}
