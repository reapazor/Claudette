using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.Core.Tests.Sessions;

public class PermissionRequestTests
{
    [Theory]
    [InlineData("Bash(dotnet test:*)", "Bash", "dotnet test:*")]
    [InlineData("  Read  ", "Read", null)]
    [InlineData("WebFetch(domain:example.com)", "WebFetch", "domain:example.com")]
    [InlineData("Bash(echo (a))", "Bash", "echo (a)")]
    public void Rules_parse(string text, string tool, string? content)
    {
        Assert.True(PermissionRule.TryParse(text, out var rule));
        Assert.Equal(new PermissionRule(tool, content), rule);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bash(unclosed")]
    [InlineData("two words")]
    [InlineData("(no tool)")]
    public void Bad_rules_are_rejected(string text) => Assert.False(PermissionRule.TryParse(text, out _));

    [Fact]
    public void A_list_splits_on_commas_outside_parentheses()
    {
        Assert.True(PermissionRule.TryParseList("Bash(git log --format=%h,%s), Read\nEdit", out var rules));
        Assert.Equal(["Bash(git log --format=%h,%s)", "Read", "Edit"], rules.Select(r => r.ToString()));
        Assert.False(PermissionRule.TryParseList(" , ", out _));
    }

    [Fact]
    public void Suggestions_give_the_rule_and_the_mode()
    {
        var request = Request("""
            {"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"touch c.txt"},"permission_suggestions":[
              {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"touch c.txt"}],"behavior":"allow","destination":"localSettings"},
              {"type":"addDirectories","directories":["/tmp/work"],"destination":"session"},
              {"type":"setMode","mode":"acceptEdits","destination":"session"}]}
            """);

        Assert.Equal([new PermissionRule("Bash", "touch c.txt")], request.SuggestedRules);
        Assert.Equal("acceptEdits", request.SuggestedMode);
    }

    [Fact]
    public async Task Always_allow_sends_the_edited_rule_and_directories_to_the_destination()
    {
        var request = Request("""
            {"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"npm test"},"permission_suggestions":[
              {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow","destination":"localSettings"},
              {"type":"addDirectories","directories":["/tmp/work"],"destination":"session"}]}
            """);

        request.AllowAlways([new PermissionRule("Bash", "npm test:*")], PermissionRequest.LocalSettings);

        var response = ((AllowDecision)await request.Decision).ToResponse();
        Assert.Equal(
            """{"behavior":"allow","updatedInput":{"command":"npm test"},"updatedPermissions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test:*"}],"behavior":"allow","destination":"localSettings"},{"type":"addDirectories","directories":["/tmp/work"],"destination":"session"}]}""",
            response.ToJsonString());
    }

    [Fact]
    public async Task Session_only_keeps_directory_suggestions_to_the_session()
    {
        var request = Request("""
            {"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},"permission_suggestions":[
              {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"ls"}],"behavior":"allow","destination":"localSettings"},
              {"type":"addDirectories","directories":["/tmp/work"],"destination":"localSettings"}]}
            """);

        request.AllowAlways([new PermissionRule("Bash", "ls")], PermissionRequest.Session);

        var updates = ((AllowDecision)await request.Decision).ToResponse()["updatedPermissions"]!.AsArray();
        Assert.All(updates, u => Assert.Equal("session", u!["destination"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Allow_and_set_mode_sends_a_session_mode_change()
    {
        var request = Request("""{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"Do it"}}""");

        request.AllowAndSetMode("acceptEdits");

        var response = ((AllowDecision)await request.Decision).ToResponse();
        Assert.Equal("""[{"type":"setMode","mode":"acceptEdits","destination":"session"}]""", response["updatedPermissions"]!.ToJsonString());
    }

    [Fact]
    public void Safety_hints_are_read_in_either_spelling()
    {
        var snake = Request("""{"subtype":"can_use_tool","tool_name":"Bash","input":{},"default_to_no":true,"suppress_always_allow_rule":true,"decision_reason":"Outside the folder"}""");
        var camel = Request("""{"subtype":"can_use_tool","tool_name":"Bash","input":{},"defaultToNo":true,"suppressAlwaysAllowRule":true}""");

        Assert.True(snake.DefaultToNo);
        Assert.True(snake.SuppressAlwaysAllowRule);
        Assert.Equal("Outside the folder", snake.DecisionReason);
        Assert.True(camel.DefaultToNo);
        Assert.True(camel.SuppressAlwaysAllowRule);
    }

    private static PermissionRequest Request(string requestJson)
    {
        var request = JsonNode.Parse(requestJson)!.AsObject();
        return new PermissionRequest(new ControlRequestMessage("r1", "can_use_tool", request, new JsonObject()));
    }
}
