using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.Core.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.App.Tests;

/// <summary>An MCP server asking for input: the card in the conversation (DESIGN.md §7, "MCP servers asking for input").</summary>
public sealed class McpInputTests : IAsyncDisposable
{
    private readonly ScriptedTransport _transport = new();
    private readonly ClaudeSession _session;
    private readonly ObservableCollection<ConversationItem> _items = [];
    private readonly ConversationBuilder _builder;
    private readonly List<string> _opened = [];

    public McpInputTests()
    {
        _session = new ClaudeSession(_transport.ForSession(), new FakeTimeProvider()) { ShowsElicitations = true };
        _builder = new ConversationBuilder(_items) { OpenUrl = url => { _opened.Add(url); return Task.CompletedTask; } };
    }

    private async Task<McpInputItem> ShowAsync(string request)
    {
        _transport.Emit($$"""{"type":"control_request","request_id":"e1","request":{{request}}}""");
        while (true)
        {
            var e = await _session.Events.ReadAsync(TestContext.Current.CancellationToken);
            if (e is ElicitationRequested requested)
            {
                _builder.Apply(requested);
                return Assert.IsType<McpInputItem>(Assert.Single(_items));
            }
        }
    }

    private JsonObject? Answer() => _transport.Sent.LastOrDefault(m => m["type"]?.GetValue<string>() == "control_response")?["response"]?["response"]?.AsObject();

    [Fact]
    public async Task A_form_is_built_from_the_schema_and_checked_before_it_is_sent()
    {
        var card = await ShowAsync("""
            {"subtype":"elicitation","mcp_server_name":"tickets","message":"Which ticket?","mode":"form","requested_schema":{"type":"object",
             "properties":{
               "project":{"type":"string","title":"Project","minLength":2},
               "count":{"type":"integer","minimum":1,"maximum":5},
               "urgent":{"type":"boolean","default":true},
               "kind":{"type":"string","enum":["bug","task"],"enumNames":["Bug","Task"]}},
             "required":["project","kind"]}}
            """);
        Assert.Equal("tickets asks", card.Title);
        Assert.Equal(["Project", "count", "urgent", "kind"], card.Fields.Select(f => f.Label));
        var (project, count, urgent, kind) = (card.Fields[0], card.Fields[1], card.Fields[2], card.Fields[3]);
        Assert.Equal(McpFieldKind.Integer, count.Kind);
        Assert.True(urgent.IsChecked);
        Assert.Equal(["Bug", "Task"], kind.Choices.Select(c => c.Label));

        // Missing and out of range: nothing is sent.
        count.Text = "9";
        card.SendCommand.Execute(null);
        Assert.True(card.IsPending);
        Assert.Equal("Required.", project.Error);
        Assert.Equal("From 1 to 5.", count.Error);
        Assert.Equal("Choose one.", kind.Error);
        Assert.Null(Answer());

        project.Text = "NX";
        count.Text = "3";
        kind.Selected = kind.Choices[1];
        card.SendCommand.Execute(null);

        await TabTestHarness.Eventually(() => Answer() is not null, "the answer");
        Assert.False(card.IsPending);
        Assert.Equal("Sent", card.Outcome);
        Assert.Equal("""{"action":"accept","content":{"project":"NX","count":3,"urgent":true,"kind":"task"}}""", Answer()!.ToJsonString());
    }

    [Fact]
    public async Task A_link_opens_in_the_browser_and_the_server_saying_its_done_finishes_the_card()
    {
        var card = await ShowAsync("""{"subtype":"elicitation","mcp_server_name":"auth","message":"Sign in to continue","mode":"url","url":"https://example.com/login","elicitation_id":"el-1"}""");
        Assert.True(card.IsUrl);

        await card.OpenUrlCommand.ExecuteAsync(null);
        Assert.Equal(["https://example.com/login"], _opened);
        Assert.True(card.HasOpenedUrl);

        _builder.Apply(new SystemNotice(new Core.Protocol.SystemMessage("elicitation_complete", JsonNode.Parse("""{"type":"system","subtype":"elicitation_complete","mcp_server_name":"auth","elicitation_id":"el-1"}""")!.AsObject())));

        Assert.Equal("Done", card.Outcome);
        await TabTestHarness.Eventually(() => Answer() is not null, "the answer");
        Assert.Equal("accept", Answer()!["action"]!.GetValue<string>());
    }

    [Fact]
    public async Task Declining_or_a_withdrawn_request_ends_the_card()
    {
        var card = await ShowAsync("""{"subtype":"elicitation","mcp_server_name":"tickets","message":"Pick one","requested_schema":{"type":"object","properties":{}}}""");

        card.DeclineCommand.Execute(null);

        await TabTestHarness.Eventually(() => Answer() is not null, "the answer");
        Assert.Equal("decline", Answer()!["action"]!.GetValue<string>());
        Assert.False(card.DeclineCommand.CanExecute(null));

        _builder.Apply(new ElicitationCancelled("e1"));
        Assert.Equal("Declined", card.Outcome);
    }

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}
