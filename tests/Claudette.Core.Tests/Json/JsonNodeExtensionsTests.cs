using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Tests.Json;

/// <summary>Reading protocol JSON tolerantly (CLAUDE.md, "Parse tolerantly"), whether it was parsed or built in code.</summary>
public class JsonNodeExtensionsTests
{
    [Fact]
    public void Numbers_read_the_same_parsed_or_built_and_anything_else_is_null()
    {
        var parsed = JsonNode.Parse("""{ "n": 13, "f": 2.5, "s": "13", "big": 1e400 }""")!.AsObject();
        var built = new JsonObject { ["n"] = 13, ["f"] = 2.5 };

        Assert.Equal(13, parsed.GetDouble("n"));
        Assert.Equal(13, built.GetDouble("n"));
        Assert.Equal(2.5, built.GetDouble("f"));
        Assert.Null(parsed.GetDouble("s"));
        Assert.Null(parsed.GetDouble("missing"));
        Assert.Equal(13, parsed.GetLong("n"));
        Assert.Null(parsed.GetLong("f"));
    }

    [Fact]
    public void A_whole_number_is_cut_from_any_number()
    {
        Assert.Equal(13, JsonNode.Parse("13").AsWholeNumber());
        Assert.Equal(2, JsonNode.Parse("2.9").AsWholeNumber());
        Assert.Equal(7, JsonValue.Create(7).AsWholeNumber());
        Assert.Null(JsonNode.Parse("\"7\"").AsWholeNumber());
        Assert.Null(((JsonNode?)null).AsWholeNumber());
    }

    [Fact]
    public void An_id_can_be_a_string_or_a_number()
    {
        var obj = JsonNode.Parse("""{ "a": "x1", "b": 7, "c": true }""")!.AsObject();

        Assert.Equal("x1", obj.GetStringOrNumber("a"));
        Assert.Equal("7", obj.GetStringOrNumber("b"));
        Assert.Null(obj.GetStringOrNumber("c"));
    }
}
