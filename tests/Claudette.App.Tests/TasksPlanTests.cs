using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// The plan at the head of the Tasks page (DESIGN.md §5, "Tasks"): the one the user approved, however they approved it,
/// or a reply they chose with <b>Show as the plan</b>.
/// </summary>
public class TasksPlanTests
{
    private const string PlanText = "1. Read the build script\n2. Fix the config";

    private static string PlanRequest(string requestId, string toolUseId) => new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = new JsonObject
        {
            ["subtype"] = "can_use_tool", ["tool_name"] = "ExitPlanMode", ["tool_use_id"] = toolUseId,
            ["input"] = new JsonObject { ["plan"] = PlanText, ["planFilePath"] = "plan.md" },
        },
    }.ToJsonString();

    private static string Approved(string toolUseId) =>
        Wire.Result(toolUseId, "User has approved your plan. You can now start coding.", toolUseResult: new JsonObject { ["plan"] = PlanText, ["isAgent"] = false });

    [Fact]
    public async Task A_plan_approved_on_its_card_heads_the_tasks_page()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Tool(null, "t1", "ExitPlanMode", new JsonObject()));
        h.Transport.Emit(PlanRequest("p1", "t1"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<PlanItem>().Any(), "the plan card");

        tab.Items.OfType<PlanItem>().Single().ApproveAcceptingEditsCommand.Execute(null);
        h.Transport.Emit(Approved("t1"));

        await TabTestHarness.Eventually(() => tab.TodoList.HasPlan, "the plan");
        Assert.Equal(PlanText, tab.TodoList.Plan);
        Assert.Equal(PlanSource.Approved, tab.TodoList.PlanSource);
        Assert.Equal(h.Time.GetUtcNow(), tab.TodoList.PlanAt);
    }

    [Fact]
    public async Task A_plan_approved_in_the_Claude_app_heads_the_tasks_page_too()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Tool(null, "t1", "ExitPlanMode", new JsonObject()));
        h.Transport.Emit(PlanRequest("p1", "t1"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<PlanItem>().Any(), "the plan card");

        // Answered on the phone: Claude Code withdraws the card here, then runs the tool.
        h.Transport.Emit("""{"type":"control_cancel_request","request_id":"p1"}""");
        h.Transport.Emit(Approved("t1"));

        await TabTestHarness.Eventually(() => tab.TodoList.HasPlan, "the plan");
        Assert.Equal(PlanText, tab.TodoList.Plan);
        Assert.False(tab.Items.OfType<PlanItem>().Single().IsPending);
    }

    [Fact]
    public async Task Show_as_the_plan_puts_a_reply_on_the_tasks_page_and_keeps_it_with_the_tab()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Text(null, $"Here's my plan for #16.\n\n{PlanText}\n"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Any(r => !r.IsStreaming), "the reply");
        var reply = tab.Items.OfType<AssistantTextItem>().Single();
        Assert.False(tab.TodoList.HasAnything);

        tab.ShowAsPlanCommand.Execute(reply);

        Assert.Equal($"Here's my plan for #16.\n\n{PlanText}", tab.TodoList.Plan);
        Assert.Equal(PlanSource.Reply, tab.TodoList.PlanSource);
        Assert.Equal(reply.SentAt, tab.TodoList.PlanAt);
        Assert.True(tab.TodoList.HasAnything);
        Assert.True(tab.IsSidePanelOpen);
        Assert.True(tab.IsTasksPage);
        Assert.Equal(tab.TodoList.Plan, tab.State.Plan?.Text);
        Assert.Equal(h.Time.GetUtcNow(), tab.State.Plan?.ChosenAt);

        // /clear forgets it, as it forgets the tasks.
        h.Transport.Emit("""{"type":"conversation_reset","trigger":"clear"}""");
        await TabTestHarness.Eventually(() => !tab.TodoList.HasPlan, "the cleared conversation");
        Assert.Null(tab.State.Plan);
    }

    public enum Transcript
    {
        WithTheReply,
        WithoutTheReply,
        WithAPlanApprovedLater,
    }

    [Theory]
    [InlineData(Transcript.WithTheReply)]
    [InlineData(Transcript.WithoutTheReply)]
    [InlineData(Transcript.WithAPlanApprovedLater)]
    public async Task A_restored_tab_shows_its_chosen_plan_while_the_reply_is_there_and_nothing_was_approved_since(Transcript transcript)
    {
        await using var h = new TabTestHarness();
        var reply = transcript == Transcript.WithoutTheReply ? "Something else." : PlanText;
        List<string> lines =
        [
            Wire.Entry("user", "2026-09-27T09:30:00Z", new JsonObject { ["role"] = "user", ["content"] = "Plan #16" }),
            Wire.Entry("assistant", "2026-09-27T09:31:00Z", Wire.Message(new JsonObject { ["type"] = "text", ["text"] = reply })),
        ];
        if (transcript == Transcript.WithAPlanApprovedLater)
        {
            lines.Add(Wire.Entry("assistant", "2026-09-27T09:50:00Z", Wire.Message(new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = "ExitPlanMode", ["input"] = new JsonObject() })));
            lines.Add(Wire.Entry("user", "2026-09-27T09:55:00Z", Wire.ResultMessage("t1", "User has approved your plan."), new JsonObject { ["plan"] = "The approved one", ["isAgent"] = false }));
        }
        h.WriteTranscript("s1", [.. lines]);
        var written = DateTimeOffset.Parse("2026-09-27T09:31:00Z", System.Globalization.CultureInfo.InvariantCulture);
        h.Services.State.Tabs =
        [
            new TabState
            {
                Folder = h.WorkFolder, IsPinned = true, SessionId = "s1",
                Plan = new ChosenPlan { Text = PlanText, WrittenAt = written, ChosenAt = written.AddMinutes(5) },
            },
        ];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.Items.OfType<AssistantTextItem>().Any(), "the restored conversation");

        switch (transcript)
        {
            case Transcript.WithTheReply:
                Assert.Equal(PlanText, tab.TodoList.Plan);
                Assert.Equal(PlanSource.Reply, tab.TodoList.PlanSource);
                Assert.Equal(written, tab.TodoList.PlanAt);
                Assert.NotNull(tab.State.Plan);
                break;
            case Transcript.WithoutTheReply:
                // Gone back past it: a rewind, say.
                Assert.False(tab.TodoList.HasPlan);
                Assert.Null(tab.State.Plan);
                break;
            case Transcript.WithAPlanApprovedLater:
                Assert.Equal("The approved one", tab.TodoList.Plan);
                Assert.Equal(PlanSource.Approved, tab.TodoList.PlanSource);
                Assert.Null(tab.State.Plan);
                break;
        }
    }
}
