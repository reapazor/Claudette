using System.Text.Json;
using Claudette.Core.Json;

namespace Claudette.Core.Tests.Json;

/// <summary>
/// Reading JSON that <c>JsonNode.Parse</c> accepts but then throws on: a repeated key, which Claude Code (like
/// JavaScript's <c>JSON.parse</c>) reads as its last value.
/// </summary>
public sealed class JsonTreeTests
{
    [Fact]
    public void A_repeated_key_reads_as_its_last_value_at_any_depth()
    {
        var root = JsonTree.ParseObject("""{ "a": 1, "a": 2, "b": { "c": "x", "c": "y" }, "list": [ { "d": true, "d": false } ] }""")!;

        Assert.Equal(2, root["a"]!.GetValue<int>());
        Assert.Equal("y", root["b"]!["c"]!.GetValue<string>());
        Assert.False(root["list"]![0]!["d"]!.GetValue<bool>());
        Assert.Equal(3, root.Count);
    }

    [Fact]
    public void Values_keep_their_kinds()
    {
        var root = JsonTree.ParseObject("""{ "s": "t", "n": 1.5, "t": true, "z": null, "o": {}, "e": [], "s": "u" }""")!;

        Assert.Equal(JsonValueKind.String, root["s"]!.GetValueKind());
        Assert.Equal(1.5, root["n"]!.GetValue<double>());
        Assert.True(root["t"]!.GetValue<bool>());
        Assert.True(root.ContainsKey("z"));
        Assert.Null(root["z"]);
        Assert.Empty(root["o"]!.AsObject());
        Assert.Empty(root["e"]!.AsArray());
        Assert.Equal("""{"s":"u","n":1.5,"t":true,"z":null,"o":{},"e":[]}""", root.ToJsonString());
    }

    [Fact]
    public void Comments_and_trailing_commas_are_still_allowed_when_asked_for_with_a_repeated_key()
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        var root = JsonTree.ParseObject("""
            {
              // the user's choice
              "permissions": { "defaultMode": "plan", },
              "permissions": { "defaultMode": "acceptEdits", },
            }
            """, options)!;

        Assert.Equal("acceptEdits", root["permissions"]!["defaultMode"]!.GetValue<string>());
    }

    [Fact]
    public void Text_that_isnt_JSON_still_throws_a_JsonException()
    {
        Assert.ThrowsAny<JsonException>(() => JsonTree.Parse("{ \"a\": "));
        Assert.ThrowsAny<JsonException>(() => JsonTree.Parse("{ \"a\": 1, \"a\": }"));
    }

    [Fact]
    public void Something_other_than_an_object_reads_as_no_object()
    {
        Assert.Null(JsonTree.ParseObject("[1, 2]"));
        Assert.Null(JsonTree.ParseObject("null"));
    }
}
