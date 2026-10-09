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

        await TabTestHarness.Eventually(() => tab.TodoList.NewestPlan?.State == PlanState.Approved, "the approved plan");
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

        await TabTestHarness.Eventually(() => tab.TodoList.NewestPlan?.State == PlanState.Approved, "the approved plan");
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

    private static string PlanRequestFor(string requestId, string toolUseId, string? plan)
    {
        var input = new JsonObject { ["planFilePath"] = "plan.md" };
        if (plan is not null)
        {
            input["plan"] = plan;
        }
        return new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = "ExitPlanMode", ["tool_use_id"] = toolUseId, ["input"] = input },
        }.ToJsonString();
    }

    private const string PlanMode = """{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"plan"}""";

    [Fact]
    public async Task The_plan_shows_as_Claude_writes_it_in_Plan_mode_and_the_draft_is_the_version_put_up()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(PlanMode);
        var file = Path.Combine(h.WorkFolder, "plans", "brisk-otter.md");
        h.Transport.Emit(Wire.Tool(null, "w1", "Write", new JsonObject { ["file_path"] = file, ["content"] = "1. Read the build script\n" }));
        h.Transport.Emit(Wire.Result("w1", "File created", toolUseResult: new JsonObject { ["type"] = "create", ["filePath"] = file }));
        await TabTestHarness.Eventually(() => tab.TodoList.IsDrafting, "the draft");

        Assert.Equal("1. Read the build script", tab.TodoList.Plan);
        Assert.Equal("Drafting the plan…", tab.TodoList.StatusText);
        Assert.StartsWith("Drafting since ", tab.TodoList.ShownPlan!.TimeText, StringComparison.Ordinal);
        Assert.True(tab.TodoList.HasAnything);

        // An Edit is made to the file as its result had it.
        h.Transport.Emit(Wire.Tool(null, "e1", "Edit", new JsonObject { ["file_path"] = file, ["old_string"] = "script", ["new_string"] = "script\n2. Fix the config" }));
        h.Transport.Emit(Wire.Result("e1", "ok", toolUseResult: new JsonObject { ["filePath"] = file, ["originalFile"] = "1. Read the build script\n" }));
        await TabTestHarness.Eventually(() => tab.TodoList.Plan?.Contains("Fix the config", StringComparison.Ordinal) == true, "the edited draft");
        Assert.Equal("1. Read the build script\n2. Fix the config", tab.TodoList.Plan);

        // Put up for review without a plan of its own: the draft stands in, and stays one version.
        h.Transport.Emit(Wire.Tool(null, "t1", "ExitPlanMode", new JsonObject()));
        h.Transport.Emit(PlanRequestFor("p1", "t1", plan: null));
        await TabTestHarness.Eventually(() => tab.Items.OfType<PlanItem>().Any(), "the plan card");
        var card = tab.Items.OfType<PlanItem>().Single();
        Assert.True(card.HasPlan);
        Assert.Equal("1. Read the build script\n2. Fix the config", card.Plan.ToString());
        Assert.Equal("Claude's plan", card.Title);
        var version = Assert.Single(tab.TodoList.Plans);
        Assert.Equal(PlanState.Waiting, version.State);
        Assert.Null(tab.TodoList.StatusText);
    }

    [Fact]
    public async Task A_Markdown_file_written_outside_Plan_mode_isnt_a_plan()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"acceptEdits"}""");
        var file = Path.Combine(h.WorkFolder, "README.md");
        h.Transport.Emit(Wire.Tool(null, "w1", "Write", new JsonObject { ["file_path"] = file, ["content"] = "# Readme" }));
        h.Transport.Emit(Wire.Result("w1", "File created", toolUseResult: new JsonObject { ["type"] = "create", ["filePath"] = file }));
        await TabTestHarness.Eventually(() => tab.Items.OfType<ToolUseItem>().Any(t => t.IsComplete), "the write");

        Assert.Empty(tab.TodoList.Plans);
        Assert.False(tab.TodoList.HasAnything);
    }

    [Fact]
    public async Task A_plan_sent_back_keeps_its_feedback_and_the_revision_is_the_next_version_with_its_changes()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(PlanMode);
        h.Transport.Emit(Wire.Tool(null, "t1", "ExitPlanMode", new JsonObject()));
        h.Transport.Emit(PlanRequestFor("p1", "t1", "1. Read\n2. Rewrite the API"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<PlanItem>().Any(), "the first card");

        var first = tab.Items.OfType<PlanItem>().Single();
        first.StartFeedbackCommand.Execute(null);
        first.Feedback = "keep the old API";
        first.KeepPlanningCommand.Execute(null);
        h.Transport.Emit(Wire.Result("t1", "The user wants to keep planning and said: keep the old API", isError: true));
        await TabTestHarness.Eventually(() => tab.TodoList.NewestPlan?.State == PlanState.SentBack, "the plan sent back");

        var v1 = tab.TodoList.NewestPlan!;
        Assert.Equal("keep the old API", v1.Feedback);
        Assert.StartsWith("Sent back ", v1.TimeText, StringComparison.Ordinal);

        h.Transport.Emit(Wire.Tool(null, "t2", "ExitPlanMode", new JsonObject()));
        h.Transport.Emit(PlanRequestFor("p2", "t2", "1. Read\n2. Extend the API"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<PlanItem>().Count() == 2, "the second card");

        var second = tab.Items.OfType<PlanItem>().Last();
        Assert.Equal("Claude's plan · v2", second.Title);
        Assert.True(second.HasChanges);
        Assert.Equal("Changes from v1", second.ChangesLabel);
        Assert.Equal((1, 1), (second.Changes!.Added, second.Changes.Removed));
        Assert.Contains(second.Changes.Lines, l => l is { IsRemoved: true, Text: "2. Rewrite the API" });
        Assert.Contains(second.Changes.Lines, l => l is { IsAdded: true, Text: "2. Extend the API" });
        second.ShowChanges = true;
        Assert.False(second.ShowsPlan);

        // The page follows the newest, and steps back through the others.
        Assert.Equal(2, tab.TodoList.Plans.Count);
        Assert.Same(tab.TodoList.Plans[1], tab.TodoList.ShownPlan);
        Assert.Equal("v2 of 2", tab.TodoList.ShownPlanPosition);
        tab.TodoList.ShowEarlierPlanCommand.Execute(null);
        Assert.Same(v1, tab.TodoList.ShownPlan);
        Assert.Equal("v1 of 2", tab.TodoList.ShownPlanPosition);
        Assert.False(tab.TodoList.ShowEarlierPlanCommand.CanExecute(null));
        tab.TodoList.ShowPlanChanges = true;
        Assert.False(tab.TodoList.ShowsPlanChanges);
        tab.TodoList.ShowLaterPlanCommand.Execute(null);
        Assert.True(tab.TodoList.ShowsPlanChanges);
    }

    [Fact]
    public async Task A_plan_cut_off_before_its_answered_says_so()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(PlanMode);
        h.Transport.Emit(Wire.Tool(null, "t1", "ExitPlanMode", new JsonObject()));
        h.Transport.Emit(PlanRequestFor("p1", "t1", PlanText));
        await TabTestHarness.Eventually(() => tab.TodoList.NewestPlan?.State == PlanState.Waiting, "the plan waiting");

        h.Transport.Emit("""{"type":"control_cancel_request","request_id":"p1"}""");
        h.Transport.Emit(Wire.Result("t1", "Interrupted", isError: true, interrupted: true));
        await TabTestHarness.Eventually(() => tab.TodoList.NewestPlan?.State == PlanState.Unanswered, "the plan not answered");
        Assert.Equal("Not answered", tab.TodoList.NewestPlan!.TimeText);
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
