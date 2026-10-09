using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Threads;

namespace Claudette.App.Tests;

/// <summary>
/// Threads (DESIGN.md §18): a tab whose Claude hands work to other tabs in its group through Claude Code's own
/// <c>SendMessage</c>, which Claudette's PreToolUse hook delivers, and gets one report back once they've finished. Each
/// tab runs on a scripted process of its own: the thread on the first, its sub-threads on the next.
/// </summary>
public class ThreadTests
{
    [Fact]
    public async Task A_sub_thread_sits_under_its_thread_and_both_are_saved()
    {
        await using var h = new TabTestHarness();
        h.Factory.ProcessPerSession = true;
        var other = await ThreadScript.OpenAsync(h, "Other");
        var plan = await ThreadScript.OpenAsync(h, "Plan");
        var art = await ThreadScript.OpenAsync(h, "Art page");

        h.Shell.MakeThreadCommand.Execute(plan);
        Assert.Equal([plan], other.AssignableThreads.Select(t => t.Thread));
        art.AssignToThreadCommand.Execute(plan);

        Assert.True(plan.IsThread);
        Assert.Equal(plan, art.ThreadHead);
        Assert.Equal([art], plan.SubThreads);
        Assert.Equal(["Other", "Plan", "Art page"], h.Shell.Groups.Single().Tabs.Select(t => t.DisplayName));
        Assert.True(plan.State.IsThread);
        Assert.Equal(plan.Id, art.State.ThreadId);
        Assert.False(art.CanMakeThread);
        Assert.Empty(art.AssignableThreads);

        // Moving the thread up takes its sub-thread along; the sub-thread can't leave it.
        h.Shell.MoveTabUpCommand.Execute(plan);
        Assert.Equal(["Plan", "Art page", "Other"], h.Shell.Groups.Single().Tabs.Select(t => t.DisplayName));
        h.Shell.MoveTabTo(art, 2);
        Assert.Equal(["Plan", "Art page", "Other"], h.Shell.Groups.Single().Tabs.Select(t => t.DisplayName));
    }

    [Fact]
    public async Task The_hook_delivers_to_an_idle_sub_thread_and_tells_Claude_it_went()
    {
        await using var h = new TabTestHarness();
        var (plan, art) = await ThreadAsync(h);

        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "art page", "Build the art page."));
        var answer = await HookAnswerAsync(h, 0, "hk1");

        Assert.Equal("deny", answer["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>());
        Assert.Equal(ThreadMessages.Delivered("Art page", waits: false), answer["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>());
        await TabTestHarness.Eventually(() => h.Transport.SentUserTextsTo(1).Any(t => t.StartsWith("Message from the thread \"Plan\"", StringComparison.Ordinal)), "the delivery");
        Assert.EndsWith("\n\nBuild the art page.", h.Transport.SentUserTextsTo(1).Last(), StringComparison.Ordinal);
        var card = art.Items.OfType<UserMessageItem>().Last();
        Assert.Equal("Build the art page.", card.Text);
        Assert.Equal("From the thread Plan", card.AutomaticLabel);
        Assert.True(card.IsFromThread);
    }

    [Fact]
    public async Task A_busy_sub_thread_gets_the_message_once_its_turn_ends()
    {
        await using var h = new TabTestHarness();
        var (_, art) = await ThreadAsync(h);
        h.Transport.EmitTo(1, ThreadScript.Init());
        await TabTestHarness.Eventually(() => art.Status == TabStatus.Working, "the sub-thread to work");
        var sentBefore = h.Transport.SentUserTextsTo(1).Count();

        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page", "Then the gallery."));
        var answer = await HookAnswerAsync(h, 0, "hk1");

        Assert.Equal(ThreadMessages.Delivered("Art page", waits: true), answer["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>());
        Assert.True(art.Items.OfType<UserMessageItem>().Last().IsHeld);
        Assert.Equal(sentBefore, h.Transport.SentUserTextsTo(1).Count());

        h.Transport.EmitTo(1, Result("Its own work is done."));
        await TabTestHarness.Eventually(() => h.Transport.SentUserTextsTo(1).Count() == sentBefore + 1, "the held message to go");
        Assert.EndsWith("Then the gallery.", h.Transport.SentUserTextsTo(1).Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_first_waits_on_the_user()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await ThreadAsync(h, ask: true);

        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page", "Build it."));
        await TabTestHarness.Eventually(() => plan.ThreadApprovals.Count == 1, "the approval");
        var approval = plan.ThreadApprovals.Single();
        Assert.Equal("Send this to Art page?", approval.Title);
        Assert.Equal(TabStatus.NeedsInput, plan.Status);
        Assert.True(plan.HasThreadStrip);
        Assert.Equal("Claude wants to send a message to Art page.", h.Notifier.Last("NeedsInput")?.Body);
        Assert.DoesNotContain(h.Transport.SentUserTextsTo(1), t => t.Contains("Build it.", StringComparison.Ordinal));

        approval.SendCommand.Execute(null);
        var answer = await HookAnswerAsync(h, 0, "hk1");
        Assert.Equal(ThreadMessages.Delivered("Art page", waits: false), answer["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>());
        await TabTestHarness.Eventually(() => plan.ThreadApprovals.Count == 0 && plan.Status != TabStatus.NeedsInput, "the approval to go");

        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk2", "Art page", "And this."));
        await TabTestHarness.Eventually(() => plan.ThreadApprovals.Count == 1, "the second approval");
        plan.ThreadApprovals.Single().DontSendCommand.Execute(null);
        answer = await HookAnswerAsync(h, 0, "hk2");
        Assert.Equal(ThreadMessages.Declined("Art page"), answer["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>());
        Assert.DoesNotContain(h.Transport.SentUserTextsTo(1), t => t.Contains("And this.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_approval_nobody_answers_is_not_sent_after_ten_minutes()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await ThreadAsync(h, ask: true);

        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page", "Build it."));
        await TabTestHarness.Eventually(() => plan.ThreadApprovals.Count == 1, "the approval");
        // The wait starts just after the card shows: the clock moves on until it has run out.
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromMinutes(1));
            return Answer(h, 0, "hk1") is not null;
        }, "the approval to run out");

        Assert.Equal(ThreadMessages.NotAnswered("Art page"), Answer(h, 0, "hk1")!["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>());
        await TabTestHarness.Eventually(() => plan.ThreadApprovals.Count == 0, "the card to go");
    }

    [Fact]
    public async Task Anything_but_a_message_to_a_sub_thread_goes_on_as_Claude_Code_would()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await ThreadAsync(h);

        // A subagent, another session, or a tab that isn't one of its sub-threads.
        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "researcher", "Look into it."));
        Assert.Equal(["continue"], (await HookAnswerAsync(h, 0, "hk1")).Select(p => p.Key));

        // Not a thread any more: its sub-thread is an ordinary tab.
        h.Shell.StopBeingThreadCommand.Execute(plan);
        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk2", "Art page", "Build it."));
        Assert.Equal(["continue"], (await HookAnswerAsync(h, 0, "hk2")).Select(p => p.Key));
    }

    [Fact]
    public async Task Asking_to_be_told_when_a_sub_thread_is_idle_isnt_needed()
    {
        await using var h = new TabTestHarness();
        await ThreadAsync(h);

        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page [3fa9c1]", "", notify: true));

        Assert.Equal(ThreadMessages.NoSubscriptionNeeded("Art page"), (await HookAnswerAsync(h, 0, "hk1"))["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task One_report_comes_back_once_every_sub_thread_messaged_has_finished()
    {
        await using var h = new TabTestHarness();
        var (plan, art) = await ThreadAsync(h);
        var server = await ThreadScript.OpenAsync(h, "Server");
        server.AssignToThreadCommand.Execute(plan);
        var sentToPlan = h.Transport.SentUserTextsTo(0).Count();

        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page", "Build the art page."));
        await HookAnswerAsync(h, 0, "hk1");
        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk2", "Server", "Build the server page."));
        await HookAnswerAsync(h, 0, "hk2");
        await TabTestHarness.Eventually(() => art.IsWorking && server.IsWorking, "both to start");

        EmitTurn(h, 1, "The art page is done.");
        await TabTestHarness.Eventually(() => !art.IsWorking, "the art page's turn to end");
        Assert.Equal(sentToPlan, h.Transport.SentUserTextsTo(0).Count());
        Assert.Equal("Thread · 1 of 2 working", plan.RowDetail);

        h.Transport.EmitTo(2, ThreadScript.Init());
        h.Transport.EmitTo(2, """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"API Error: 500","session_id":"s3"}""");
        await TabTestHarness.Eventually(() => h.Transport.SentUserTextsTo(0).Count() == sentToPlan + 1, "the report");

        var report = h.Transport.SentUserTextsTo(0).Last();
        Assert.Equal(ThreadMessages.Report(
        [
            new SubThreadReport("Art page", SubThreadOutcome.Finished, "The art page is done."),
            new SubThreadReport("Server", SubThreadOutcome.Failed, null, "API Error: 500"),
        ]), report);
        var card = plan.Items.OfType<UserMessageItem>().Last();
        Assert.Equal("From the sub-threads", card.AutomaticLabel);
    }

    [Fact]
    public async Task A_sub_thread_closed_before_it_finished_is_in_the_report()
    {
        await using var h = new TabTestHarness(s => s.General.ConfirmCloseWorkingTab = false);
        var (plan, art) = await ThreadAsync(h);
        var sentToPlan = h.Transport.SentUserTextsTo(0).Count();
        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page", "Build the art page."));
        await HookAnswerAsync(h, 0, "hk1");

        await h.Shell.CloseTabCommand.ExecuteAsync(art);

        await TabTestHarness.Eventually(() => h.Transport.SentUserTextsTo(0).Count() == sentToPlan + 1, "the report");
        Assert.Contains("Its tab was closed before it finished.", h.Transport.SentUserTextsTo(0).Last(), StringComparison.Ordinal);
        Assert.Empty(plan.SubThreads);
    }

    [Fact]
    public async Task A_thread_that_stops_being_one_gets_no_report_for_work_under_way()
    {
        await using var h = new TabTestHarness();
        var (plan, art) = await ThreadAsync(h);
        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page", "Build the art page."));
        await HookAnswerAsync(h, 0, "hk1");
        await TabTestHarness.Eventually(() => art.IsWorking, "the sub-thread to start");

        h.Shell.StopBeingThreadCommand.Execute(plan);
        EmitTurn(h, 1, "The art page is done.");

        // The moment a report would have gone: the sub-thread's turn has ended.
        await TabTestHarness.Eventually(() => !art.IsWorking, "the sub-thread's turn to end");
        Assert.Empty(h.Transport.SentUserTextsTo(0));
        Assert.Null(art.ThreadHead);
    }

    [Fact]
    public async Task A_sub_thread_waiting_on_the_user_can_be_answered_from_the_thread()
    {
        await using var h = new TabTestHarness();
        var (plan, art) = await ThreadAsync(h);
        h.Transport.EmitTo(1, ThreadScript.Init());

        h.Transport.EmitTo(1, ThreadScript.CanUseTool("req-1", "npm test"));
        await TabTestHarness.Eventually(() => plan.HasThreadStrip, "the strip");

        var waiting = Assert.Single(plan.WaitingSubThreads);
        Assert.Equal(art, waiting.SubThread);
        Assert.Equal("Allow this command? npm test", waiting.Text);
        Assert.True(waiting.CanAnswer);
        // Waiting on the user is the sub-thread's, not the thread's.
        Assert.NotEqual(TabStatus.NeedsInput, plan.Status);

        waiting.AllowCommand.Execute(null);

        await TabTestHarness.Eventually(() => h.Transport.SentTo(1).Any(m => m["type"]?.GetValue<string>() == "control_response"
            && m["response"]?["request_id"]?.GetValue<string>() == "req-1"
            && m["response"]?["response"]?["behavior"]?.GetValue<string>() == "allow"), "the answer");
        await TabTestHarness.Eventually(() => !plan.HasThreadStrip, "the strip to go");
        Assert.Empty(h.Transport.SentUserTextsTo(0));
    }

    [Fact]
    public async Task The_threads_next_message_names_its_sub_threads_once()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await ThreadAsync(h);

        await SendAsync(plan, "Split the Discord rework between them.");
        await SendAsync(plan, "Carry on.");

        var sent = h.Transport.SentUserTextsTo(0).ToList();
        Assert.Equal($"Split the Discord rework between them.\n\n{ThreadMessages.Note(["Art page"])}", sent[^2]);
        Assert.Equal("Carry on.", sent[^1]);
        var card = plan.Items.OfType<UserMessageItem>().First(m => m.Text.StartsWith("Split", StringComparison.Ordinal));
        Assert.Equal("Told Claude about its sub-threads", card.ThreadNoteText);
        Assert.Null(card.SuffixText);

        h.Shell.StopBeingThreadCommand.Execute(plan);
        await SendAsync(plan, "Just you now.");
        Assert.Equal($"Just you now.\n\n{ThreadMessages.NoLongerAThread}", h.Transport.SentUserTextsTo(0).Last());
    }

    [Fact]
    public async Task Threads_come_back_after_a_restore()
    {
        await using var h = new TabTestHarness();
        var plan = new Core.Settings.TabState { Folder = h.WorkFolder, UserName = "Plan", IsThread = true, AskBeforeSendingToSubThreads = false };
        var art = new Core.Settings.TabState { Folder = h.WorkFolder, UserName = "Art page", ThreadId = plan.Id };
        var lost = new Core.Settings.TabState { Folder = h.WorkFolder, UserName = "Lost", ThreadId = "gone" };
        h.Services.State.Tabs = [art, lost, plan];
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;

        h.Shell.Restore(null);

        var tabs = h.Shell.Groups.Single().Tabs;
        Assert.Equal(["Lost", "Plan", "Art page"], tabs.Select(t => t.DisplayName));
        Assert.Equal("Plan", tabs[2].ThreadHead?.DisplayName);
        Assert.False(tabs[1].AskBeforeSendingToSubThreads);
        Assert.Null(tabs[0].ThreadHead);
        Assert.Null(lost.ThreadId);
    }

    [Fact]
    public async Task Closing_a_thread_leaves_its_sub_threads_as_ordinary_tabs()
    {
        await using var h = new TabTestHarness();
        var (plan, art) = await ThreadAsync(h);

        await h.Shell.CloseTabCommand.ExecuteAsync(plan);

        Assert.Null(art.ThreadHead);
        Assert.Null(art.State.ThreadId);
        Assert.True(art.CanMakeThread);
    }

    [Fact]
    public async Task A_delivered_send_shows_as_done_on_the_threads_tool_row()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await ThreadAsync(h);

        h.Transport.EmitTo(0, ThreadScript.Init());
        h.Transport.EmitTo(0, Wire.Tool(null, "toolu_s", "SendMessage", new JsonObject { ["to"] = "Art page", ["message"] = "Build it." }));
        h.Transport.EmitTo(0, ToolResult("toolu_s", ThreadMessages.Delivered("Art page", waits: false), isError: true));
        h.Transport.EmitTo(0, Wire.Tool(null, "toolu_d", "SendMessage", new JsonObject { ["to"] = "Art page", ["message"] = "And this." }));
        h.Transport.EmitTo(0, ToolResult("toolu_d", ThreadMessages.Declined("Art page"), isError: true));
        await TabTestHarness.Eventually(() => plan.Items.OfType<ToolUseItem>().Count(t => t.IsComplete) == 2, "both results");

        var rows = plan.Items.OfType<ToolUseItem>().ToList();
        Assert.False(rows[0].IsError);
        Assert.True(rows[1].IsError);
    }

    [Fact]
    public async Task A_message_from_another_session_gets_a_card_of_its_own()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        await SendAsync(tab, "Working on it.");
        var waiting = tab.Items.OfType<UserMessageItem>().Single();

        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["isReplay"] = true,
            ["uuid"] = "peer-1",
            ["origin"] = new JsonObject { ["kind"] = "peer", ["name"] = "work-02", ["body"] = "Build the art page next.", ["from"] = "uds:pipe" },
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Another Claude session sent a message:\n<cross-session-message from-name=\"work-02\">…" },
        });

        await TabTestHarness.Eventually(() => tab.Items.OfType<UserMessageItem>().Count() == 2, "the peer's card");
        var peer = tab.Items.OfType<UserMessageItem>().Last();
        Assert.Equal("Build the art page next.", peer.Text);
        Assert.Equal("From the session work-02", peer.AutomaticLabel);
        // The card waiting for its own echo isn't taken for it.
        Assert.Null(waiting.Uuid);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A thread "Plan" on process 0 and its sub-thread "Art page" on process 1, not asking before sending unless told to.</summary>
    private static async Task<(TabViewModel Plan, TabViewModel Art)> ThreadAsync(TabTestHarness h, bool ask = false)
    {
        h.Factory.ProcessPerSession = true;
        var plan = await ThreadScript.OpenAsync(h, "Plan");
        var art = await ThreadScript.OpenAsync(h, "Art page");
        h.Shell.MakeThreadCommand.Execute(plan);
        art.AssignToThreadCommand.Execute(plan);
        if (!ask)
        {
            plan.ToggleAskBeforeSendingToSubThreadsCommand.Execute(null);
        }
        Assert.Equal(ask, plan.AskBeforeSendingToSubThreads);
        return (plan, art);
    }

    private static async Task SendAsync(TabViewModel tab, string text)
    {
        tab.ComposerText = text;
        await tab.SendCommand.ExecuteAsync(null);
    }

    private static JsonObject? Answer(TabTestHarness h, int process, string requestId) =>
        h.Transport.SentTo(process).FirstOrDefault(m => m["type"]?.GetValue<string>() == "control_response" && m["response"]?["request_id"]?.GetValue<string>() == requestId)?["response"]?["response"]?.AsObject();

    private static async Task<JsonObject> HookAnswerAsync(TabTestHarness h, int process, string requestId)
    {
        await TabTestHarness.Eventually(() => Answer(h, process, requestId) is not null, "the hook's answer");
        return Answer(h, process, requestId)!;
    }

    private static string Result(string reply) => new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = reply, ["session_id"] = "s1",
    }.ToJsonString();

    /// <summary>A whole turn on the <paramref name="process"/>th process: init, the reply and its result.</summary>
    private static void EmitTurn(TabTestHarness h, int process, string reply)
    {
        h.Transport.EmitTo(process, ThreadScript.Init());
        h.Transport.EmitTo(process, new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply }) } });
        h.Transport.EmitTo(process, Result(reply));
    }

    private static JsonObject ToolResult(string toolUseId, string text, bool isError) => new()
    {
        ["type"] = "user",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = text, ["is_error"] = isError }),
        },
    };
}
