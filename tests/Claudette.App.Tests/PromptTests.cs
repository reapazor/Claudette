using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>Permission prompts, clarifying questions and plans (DESIGN.md §7).</summary>
public class PromptTests
{
    private const string BashRequest = """
        {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"t1",
         "input":{"command":"npm test","description":"Run the tests"},
         "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow","destination":"localSettings"}]}}
        """;

    [Fact]
    public async Task Always_allow_saves_the_edited_rule_to_local_settings()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var prompt = await PromptAsync<PermissionItem>(h, tab, BashRequest);
        Assert.Equal("Allow this command?", prompt.Title);
        Assert.Equal("npm test", prompt.Command);
        Assert.Equal("Bash(npm test)", prompt.RuleText);

        prompt.StartAlwaysAllowCommand.Execute(null);
        prompt.RuleText = "Bash(npm test:*)";
        prompt.SaveRuleCommand.Execute(null);

        var reply = await ReplyAsync(h, "p1");
        Assert.Equal("allow", reply["behavior"]!.GetValue<string>());
        var update = reply["updatedPermissions"]![0]!;
        Assert.Equal("localSettings", update["destination"]!.GetValue<string>());
        Assert.Equal("npm test:*", update["rules"]![0]!["ruleContent"]!.GetValue<string>());
        Assert.Contains(".claude/settings.local.json", prompt.Outcome, StringComparison.Ordinal);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.NeedsInput, "the prompt to clear");
    }

    [Fact]
    public async Task Allow_for_this_session_only_saves_nothing()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var prompt = await PromptAsync<PermissionItem>(h, tab, BashRequest);

        prompt.AllowForSessionCommand.Execute(null);

        var reply = await ReplyAsync(h, "p1");
        Assert.Equal("session", reply["updatedPermissions"]![0]!["destination"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_invalid_rule_cant_be_saved()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var prompt = await PromptAsync<PermissionItem>(h, tab, BashRequest);

        prompt.StartAlwaysAllowCommand.Execute(null);
        prompt.RuleText = "Bash(npm test";

        Assert.True(prompt.HasRuleError);
        Assert.False(prompt.SaveRuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task Deny_with_a_message_tells_Claude_what_to_do_instead()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var prompt = await PromptAsync<PermissionItem>(h, tab, BashRequest);

        prompt.StartDenyWithMessageCommand.Execute(null);
        prompt.DenyMessage = "Run only the unit tests";
        prompt.SendDenyMessageCommand.Execute(null);

        var reply = await ReplyAsync(h, "p1");
        Assert.Equal("deny", reply["behavior"]!.GetValue<string>());
        Assert.Contains("Run only the unit tests", reply["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Denied: Run only the unit tests", prompt.Outcome);
    }

    [Fact]
    public async Task An_edit_can_switch_the_session_to_accept_edits()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var prompt = await PromptAsync<PermissionItem>(h, tab, """
            {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Write","tool_use_id":"t1",
             "input":{"file_path":"a.txt","content":"hi\n"},"permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}]}}
            """);
        Assert.False(prompt.CanAlwaysAllow);
        Assert.Equal("Allow all edits this session", prompt.ModeAction);
        Assert.True(prompt.HasDiff);

        prompt.AllowWithModeCommand.Execute(null);

        var reply = await ReplyAsync(h, "p1");
        Assert.Equal("acceptEdits", reply["updatedPermissions"]![0]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Keyboard_allow_is_refused_when_Claude_Code_says_default_to_no()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        await PromptAsync<PermissionItem>(h, tab, """
            {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf /"},"default_to_no":true}}
            """);

        Assert.False(tab.AcceptWaitingPrompt());
        Assert.True(tab.DeclineWaitingPrompt());

        var reply = await ReplyAsync(h, "p1");
        Assert.Equal("deny", reply["behavior"]!.GetValue<string>());
    }

    [Fact]
    public async Task Clarifying_questions_send_their_answers_keyed_by_question()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        // The tool row comes first; the question card replaces it.
        h.Transport.Emit("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"AskUserQuestion","input":{"questions":[]}}]}}""");
        var prompt = await PromptAsync<QuestionItem>(h, tab, """
            {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","tool_use_id":"t1","input":{"questions":[
              {"question":"Which database?","header":"Database","multiSelect":false,"options":[{"label":"Postgres","description":"Relational"},{"label":"SQLite","description":"File"}]},
              {"question":"Which parts?","header":"Parts","multiSelect":true,"options":[{"label":"API","description":""},{"label":"UI","description":""}]}]}}}
            """);
        Assert.DoesNotContain(tab.Items, i => i is ToolUseItem { Name: "AskUserQuestion" });
        Assert.False(prompt.SubmitCommand.CanExecute(null));

        prompt.Questions[0].Options[1].IsSelected = true;
        prompt.Questions[1].Options[0].IsSelected = true;
        prompt.Questions[1].OtherText = "Docs";
        prompt.SubmitCommand.Execute(null);

        var reply = await ReplyAsync(h, "p1");
        var answers = reply["updatedInput"]!["answers"]!;
        Assert.Equal("SQLite", answers["Which database?"]!.GetValue<string>());
        Assert.Equal("API, Docs", answers["Which parts?"]!.GetValue<string>());
        Assert.Equal(2, reply["updatedInput"]!["questions"]!.AsArray().Count);
    }

    [Fact]
    public async Task Typing_an_answer_clears_a_single_choice()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var prompt = await PromptAsync<QuestionItem>(h, tab, """
            {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[
              {"question":"Which?","header":"Pick","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}}}
            """);
        var question = prompt.Questions[0];

        question.Options[0].IsSelected = true;
        question.OtherText = "Neither";

        Assert.False(question.Options[0].IsSelected);
        Assert.Equal("Neither", question.Answer);
    }

    [Fact]
    public async Task Approving_a_plan_switches_mode_and_keep_planning_sends_feedback()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var plan = await PromptAsync<PlanItem>(h, tab, """
            {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"1. Read\n2. Fix"}}}
            """);
        Assert.True(plan.HasPlan);

        plan.ApproveAcceptingEditsCommand.Execute(null);
        var approved = await ReplyAsync(h, "p1");
        Assert.Equal("acceptEdits", approved["updatedPermissions"]![0]!["mode"]!.GetValue<string>());

        var second = await PromptAsync<PlanItem>(h, tab, """
            {"type":"control_request","request_id":"p2","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{}}}
            """);
        Assert.False(second.HasPlan);
        second.StartFeedbackCommand.Execute(null);
        second.Feedback = "Add tests first";
        second.KeepPlanningCommand.Execute(null);
        var kept = await ReplyAsync(h, "p2");
        Assert.Equal("deny", kept["behavior"]!.GetValue<string>());
        Assert.Contains("Add tests first", kept["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_withdrawn_request_is_marked_no_longer_needed()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var prompt = await PromptAsync<PermissionItem>(h, tab, BashRequest);

        h.Transport.Emit("""{"type":"control_cancel_request","request_id":"p1"}""");

        await TabTestHarness.Eventually(() => !prompt.IsPending, "the cancel");
        Assert.Equal("No longer needed", prompt.Outcome);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.NeedsInput, "the status to clear");
    }

    [Fact]
    public async Task A_prompt_resolved_twice_is_only_counted_off_once()
    {
        // Answered here as the Claude app answers it: Claude Code withdraws the request Claudette already answered. The
        // other prompt still waits.
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var first = await PromptAsync<PermissionItem>(h, tab, BashRequest);
        await PromptAsync<PermissionItem>(h, tab, BashRequest.Replace("\"p1\"", "\"p2\"", StringComparison.Ordinal).Replace("\"t1\"", "\"t2\"", StringComparison.Ordinal));

        first.AllowCommand.Execute(null);
        h.Transport.Emit("""{"type":"control_cancel_request","request_id":"p1"}""");
        await TabTestHarness.Eventually(() => !first.IsPending, "the answer");

        Assert.Equal(TabStatus.NeedsInput, tab.Status);
    }

    [Fact]
    public async Task Bypass_mode_needs_confirmation()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var bypass = tab.PermissionModeChoices.Single(c => c.IsBypass);

        await tab.ChooseModeCommand.ExecuteAsync(bypass);
        Assert.True(tab.IsConfirmingBypass);
        Assert.DoesNotContain("set_permission_mode", h.Transport.SentControlSubtypes);

        await tab.ConfirmBypassCommand.ExecuteAsync(null);

        Assert.Contains("set_permission_mode", h.Transport.SentControlSubtypes);
        Assert.True(tab.IsBypassMode);
        Assert.Equal("Bypass permissions", tab.PermissionModeName);
    }

    [Fact]
    public async Task A_refused_bypass_switch_explains_itself()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["set_permission_mode"] = _ => throw new InvalidOperationException("not allowed");
        var tab = await h.OpenTabAsync();

        await tab.ChooseModeCommand.ExecuteAsync(tab.PermissionModeChoices.Single(c => c.IsBypass));
        await tab.ConfirmBypassCommand.ExecuteAsync(null);

        Assert.False(tab.IsBypassMode);
        Assert.Contains(tab.Items.OfType<NoteItem>(), n => n.IsError && n.Text.Contains("Tab settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mode_changes_Claude_Code_makes_show_in_the_tab()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit("""{"type":"system","subtype":"status","permissionMode":"plan"}""");

        await TabTestHarness.Eventually(() => tab.PermissionMode == "plan", "the mode change");
        Assert.Equal("Plan", tab.PermissionModeName);
    }

    [Fact]
    public async Task A_denial_without_asking_shows_a_note()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit("""{"type":"system","subtype":"permission_denied","tool_name":"Bash","tool_use_id":"t1","decision_reason":"Matched a deny rule","message":"denied"}""");

        await TabTestHarness.Eventually(() => tab.Items.OfType<NoteItem>().Any(n => n.Text == "Claude Code denied Bash: Matched a deny rule"), "the note");
    }

    private static async Task<T> PromptAsync<T>(TabTestHarness h, TabViewModel tab, string request) where T : PromptItem
    {
        var id = JsonNode.Parse(request)!["request_id"]!.GetValue<string>();
        h.Transport.Emit(request.ReplaceLineEndings(""));
        await TabTestHarness.Eventually(() => tab.Items.OfType<T>().Any(p => p.Request.RequestId == id), $"the {typeof(T).Name}");
        Assert.Equal(TabStatus.NeedsInput, tab.Status);
        return tab.Items.OfType<T>().Single(p => p.Request.RequestId == id);
    }

    private static async Task<JsonObject> ReplyAsync(TabTestHarness h, string requestId)
    {
        JsonObject? reply = null;
        await TabTestHarness.Eventually(() =>
        {
            reply = h.Transport.Sent
                .Where(m => m["type"]?.GetValue<string>() == "control_response")
                .Select(m => m["response"]!.AsObject())
                .FirstOrDefault(r => r["request_id"]?.GetValue<string>() == requestId)?["response"]?.AsObject();
            return reply is not null;
        }, $"the reply to {requestId}");
        return reply!;
    }
}
