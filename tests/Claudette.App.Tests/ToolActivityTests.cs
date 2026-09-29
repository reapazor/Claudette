using System.Text.Json.Nodes;
using Claudette.App.Conversation;

namespace Claudette.App.Tests;

/// <summary>What a running tool is doing, in a few words for the working line (DESIGN.md §5, "Working line").</summary>
public class ToolActivityTests
{
    private static JsonObject Input(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("Bash", """{"command":"dotnet test Claudette.slnx\necho done"}""", "Running dotnet test Claudette.slnx")]
    [InlineData("PowerShell", """{"command":"Get-ChildItem"}""", "Running Get-ChildItem")]
    [InlineData("Read", """{"file_path":"/repo/src/Views/TabView.axaml"}""", "Reading TabView.axaml")]
    [InlineData("Read", """{"file_path":"C:\\repo\\App.cs"}""", "Reading App.cs")]
    [InlineData("Edit", """{"file_path":"/repo/a.cs"}""", "Editing a.cs")]
    [InlineData("Write", """{"file_path":"/repo/notes.md"}""", "Writing notes.md")]
    [InlineData("Grep", """{"pattern":"TODO"}""", "Searching for TODO")]
    [InlineData("Glob", """{"pattern":"**/*.cs"}""", "Finding **/*.cs")]
    [InlineData("WebFetch", """{"url":"https://code.claude.com/docs/en/settings"}""", "Fetching code.claude.com")]
    [InlineData("WebSearch", """{"query":"avalonia arc"}""", "Searching the web for avalonia arc")]
    [InlineData("Agent", """{"description":"Explore the auth code"}""", "Running an agent: Explore the auth code")]
    [InlineData("TodoWrite", """{"todos":[]}""", "Updating the to-do list")]
    [InlineData("mcp__github__create_issue", """{}""", "Using create_issue (github)")]
    [InlineData("SomethingNew", """{}""", "Using SomethingNew")]
    [InlineData("Bash", """{}""", "Running a command")]
    public void A_call_is_put_in_a_few_words(string name, string input, string expected) =>
        Assert.Equal(expected, ToolActivity.Describe(name, Input(input)));

    [Fact]
    public void Long_phrases_are_cut_to_one_row()
    {
        var text = ToolActivity.Describe("Bash", Input($$"""{"command":"{{new string('x', 200)}}"}"""));

        Assert.Equal(ToolActivity.MaxLength, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_calls_show_the_newest_and_how_many_more()
    {
        Assert.Null(ToolActivity.Describe([]));
        Assert.Equal("Reading b.cs and 1 more", ToolActivity.Describe([("Read", Input("""{"file_path":"a.cs"}""")), ("Read", Input("""{"file_path":"b.cs"}"""))]));
        Assert.Equal("Running 3 agents", ToolActivity.Describe([.. Enumerable.Repeat(("Agent", Input("""{"description":"x"}""")), 3)]));
    }
}
