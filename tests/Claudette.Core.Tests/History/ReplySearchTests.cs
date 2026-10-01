using Claudette.Core.History;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.History;

/// <summary>History's search through Claude's replies (DESIGN.md §9, "History").</summary>
public class ReplySearchTests
{
    private static string Line(string type, string text, bool sidechain = false) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = type,
            ["isSidechain"] = sidechain,
            ["timestamp"] = "2026-09-20T10:00:00Z",
            ["message"] = new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = type,
                ["content"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = text }),
            },
        }.ToJsonString();

    private static string Write(TempFolder temp, params string[] lines)
    {
        var path = temp.Combine("s1.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Every_word_the_prompts_lack_has_to_be_in_a_reply_and_the_first_gives_the_snippet()
    {
        using var temp = new TempFolder();
        var path = Write(temp,
            Line("user", "Why does the build fail?"),
            Line("assistant", "The linker can't find " + new string('x', 100) + " the Vulkan SDK, so set VULKAN_SDK first."),
            Line("assistant", "Then rebuild with the Shipping configuration."));

        var snippet = ReplySearch.Find(path, ["build", "vulkan", "shipping"], "Why does the build fail?", TestContext.Current.CancellationToken);

        Assert.NotNull(snippet);
        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.Contains("the Vulkan SDK", snippet, StringComparison.Ordinal);
        Assert.Null(ReplySearch.Find(path, ["vulkan", "android"], "", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Prompts_subagents_and_tool_input_dont_count_as_replies()
    {
        using var temp = new TempFolder();
        var path = Write(temp,
            Line("user", "mention vulkan here"),
            Line("assistant", "vulkan in a subagent", sidechain: true),
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"echo vulkan"}},{"type":"text","text":"Done."}]}}""");

        Assert.Null(ReplySearch.Find(path, ["vulkan"], "", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Words_search_already_found_need_nothing_from_the_replies()
    {
        using var temp = new TempFolder();
        var path = Write(temp, Line("assistant", "nothing relevant"));

        Assert.Null(ReplySearch.Find(path, ["build"], "the build", TestContext.Current.CancellationToken));
        Assert.Null(ReplySearch.Find(temp.Combine("missing.jsonl"), ["build"], "", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_snippet_is_one_line_cut_around_the_match()
    {
        var text = "first line\n\n" + new string('a', 80) + " MATCH " + new string('b', 80);

        var snippet = ReplySearch.Snippet(text, text.IndexOf("MATCH", StringComparison.Ordinal), 5);

        Assert.Equal($"…{new string('a', 59)} MATCH {new string('b', 59)}…", snippet);
    }
}
