using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

public class TabViewModelTests
{
    [Fact]
    public async Task Closing_a_tab_while_it_starts_leaves_no_claude_running()
    {
        await using var h = new TabTestHarness();
        var gate = h.Factory.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Opening waits for the start, which waits at the gate.
        var opening = h.Shell.OpenFolderAsync(h.WorkFolder);
        await TabTestHarness.Eventually(() => h.Factory.Sessions.Count == 1, "claude to start");
        var tab = h.Shell.SelectedTab!;

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await opening;

        Assert.Equal(SessionState.Exited, h.Factory.Sessions[0].State);
        Assert.Empty(h.Shell.AllTabs);
        Assert.False(gate.Task.IsCompleted);
        // Nothing starts again for it, either.
        await tab.EnsureStartedAsync();
        Assert.Single(h.Factory.Launches);
    }

    [Fact]
    public async Task Suffixes_are_appended_and_kept_chips_stay()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.AddSuffixCommand.Execute(h.Services.Suffix("clarify"));
        tab.AddSuffixCommand.Execute(h.Services.Suffix("test"));
        tab.ToggleKeepCommand.Execute(tab.Chips[1]);

        tab.ComposerText = "Fix the login bug";
        await tab.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(h.Transport.SentUserTexts);
        Assert.Equal("Fix the login bug\n\nAsk clarifying questions before you start.\nRun the relevant tests when you're done and fix any failures.", sent);
        var shown = Assert.IsType<UserMessageItem>(tab.Items[0]);
        Assert.Equal("Fix the login bug", shown.Text);
        Assert.True(shown.HasSuffix);
        var kept = Assert.Single(tab.Chips);
        Assert.Equal("test", kept.Suffix.Id);
        Assert.Equal(["test"], tab.State.KeptSuffixes);
    }

    [Fact]
    public async Task A_slash_command_gets_its_suffixes_in_a_block_before_it()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.AddSuffixCommand.Execute(h.Services.Suffix("clarify"));

        tab.ComposerText = "/review 42";
        await tab.SendCommand.ExecuteAsync(null);

        // After the command, Claude Code would take the suffix as the command's arguments (DESIGN.md §5, "Quick suffixes").
        var sent = Assert.Single(h.Transport.Sent, m => m["type"]?.GetValue<string>() == "user");
        var texts = sent["message"]!["content"]!.AsArray().Select(b => b!["text"]!.GetValue<string>());
        Assert.Equal(["Ask clarifying questions before you start.", "/review 42"], texts);
        var shown = Assert.IsType<UserMessageItem>(tab.Items[0]);
        Assert.Equal("/review 42", shown.Text);
        Assert.Equal("Ask clarifying questions before you start.", shown.SuffixText);
    }

    [Fact]
    public async Task The_first_prompt_names_the_tab()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["generate_session_title"] = _ => new JsonObject { ["title"] = "Fix login bug" };
        var tab = await h.OpenTabAsync();

        tab.ComposerText = "The login page throws on submit";
        await tab.SendCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => tab.DisplayName == "Fix login bug", "the AI title");
        Assert.Contains("generate_session_title", h.Transport.SentControlSubtypes);
    }

    [Fact]
    public async Task Without_a_title_the_tab_uses_the_prompt()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["generate_session_title"] = _ => throw new InvalidOperationException("no");
        var tab = await h.OpenTabAsync();

        tab.ComposerText = "Refactor the parser into smaller pieces please, it is getting long\nsecond line";
        await tab.SendCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => tab.State.AutoName is not null, "a fallback name");
        Assert.Equal("Refactor the parser into smaller pieces…", tab.DisplayName);
    }

    [Fact]
    public async Task A_user_name_wins_and_reset_goes_back()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.State.AutoName = "Auto";

        tab.StartRenameCommand.Execute(null);
        tab.RenameText = "Mine";
        await tab.CommitRenameCommand.ExecuteAsync(null);
        Assert.Equal("Mine", tab.DisplayName);

        tab.ResetNameCommand.Execute(null);
        Assert.Equal("Auto", tab.DisplayName);
    }

    [Fact]
    public async Task A_finished_turn_adds_tokens_and_marks_a_background_tab_unread()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.IsSelected = false;

        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Unread, "the unread status");
        Assert.Equal(120, tab.State.Tokens.Total);
        Assert.Equal("120 tok", tab.Context.TokensShort);
        Assert.Equal("s1", tab.State.SessionId);
        tab.IsSelected = true;
        Assert.Equal(TabStatus.Idle, tab.Status);
    }

    [Fact]
    public async Task Tokens_count_up_during_a_turn_and_the_context_is_estimated_without_get_context_usage()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["get_context_usage"] = _ => throw new InvalidOperationException("Unknown control request: get_context_usage");
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);

        h.Transport.Emit("""{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","content":[{"type":"text","text":"a"}],"usage":{"input_tokens":3000,"output_tokens":100,"cache_read_input_tokens":46900}}}""");
        h.Transport.Emit("""{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","content":[{"type":"text","text":"b"}],"usage":{"input_tokens":3000,"output_tokens":100,"cache_read_input_tokens":46900}}}""");
        await TabTestHarness.Eventually(() => tab.Context.TokensShort == "50k tok", "the live token count");
        // No context window known yet: nothing to estimate from.
        Assert.Null(tab.Context.Text);

        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1","modelUsage":{"claude-opus-5-5":{"inputTokens":49900,"outputTokens":100,"contextWindow":200000}}}""");

        await TabTestHarness.Eventually(() => tab.Context.Text == "Context 25%", "the estimated context");
        Assert.Equal("50k tok", tab.Context.TokensShort);
        Assert.Equal("about 50,000 of 200,000 tokens, estimated from the last call", tab.Context.Detail);
        Assert.False(tab.Context.IsHigh);

        // Claude Code says when it compacts by itself: near that, the indicator warns.
        tab.ComposerText = "more";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"autocompact_state","value":{"enabled":true,"effective_window":180000,"threshold":52000,"enforced":true,"source":"auto"},"session_id":"s1"}""");
        h.Transport.Emit("""{"type":"assistant","message":{"id":"m2","model":"claude-opus-5-5","content":[{"type":"text","text":"b"}],"usage":{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":48900}}}""");
        await TabTestHarness.Eventually(() => tab.Context.IsHigh, "the warning");
        Assert.EndsWith("auto-compacts at 52,000", tab.Context.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permission_prompt_marks_the_tab_as_needing_input_until_answered()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);

        h.Transport.Emit("""{"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf build"}}}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.NeedsInput, "needs input");

        tab.Items.OfType<PermissionItem>().Single().AllowCommand.Execute(null);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.NeedsInput, "the prompt to clear");
    }

    [Fact]
    public async Task Switching_model_waits_for_confirmation()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var haiku = tab.Models.Single(m => m.Value == "haiku");
        await tab.ChooseEffortCommand.ExecuteAsync("high");

        tab.ChooseModelCommand.Execute(haiku);
        Assert.True(tab.ModelSwitch.IsOpen);
        Assert.Contains("doesn't support high effort", tab.ModelSwitchMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("set_model", h.Transport.SentControlSubtypes);

        await tab.ModelSwitch.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("set_model", h.Transport.SentControlSubtypes);
        Assert.Equal("haiku", tab.State.Overrides.Model);
        Assert.Equal("Haiku", tab.ModelName);
        Assert.Null(tab.Effort);
    }

    [Fact]
    public async Task A_quiet_turn_gets_a_check_in()
    {
        await using var h = new TabTestHarness();
        h.Services.Settings.CheckIns = new CheckInSettings { RunTimeMinutes = 0, QuietTimeMinutes = 5, Message = "Status?" };
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");

        Advance(h, TimeSpan.FromMinutes(5));

        // It counts down first, in a bar over the composer.
        await TabTestHarness.Eventually(() => tab.HasCheckInCountdown, "the countdown");
        Assert.Equal("Checking in with Claude in 30 s.", tab.CheckInCountdownText);
        Advance(h, TimeSpan.FromSeconds(10));
        await TabTestHarness.Eventually(() => tab.CheckInCountdownText == "Checking in with Claude in 20 s.", "the countdown to go on");
        Assert.DoesNotContain("Status?", h.Transport.SentUserTexts);

        Advance(h, TimeSpan.FromSeconds(20));

        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains("Status?"), "the check-in");
        Assert.Contains(tab.Items.OfType<UserMessageItem>(), m => m.IsCheckIn);
        await TabTestHarness.Eventually(() => !tab.HasCheckInCountdown, "the bar to close");

        // The prompt the user typed says so, and the check-in doesn't: each has its own id, as its card knows it.
        var sent = h.Transport.Sent.Where(m => m["type"]?.GetValue<string>() == "user").ToList();
        var cards = tab.Items.OfType<UserMessageItem>().ToList();
        Assert.Equal("human", sent[0]["origin"]?["kind"]?.GetValue<string>());
        Assert.Null(sent[1]["origin"]);
        Assert.Equal(cards.Select(c => c.SentId), sent.Select(m => m["uuid"]?.GetValue<string>()));
        Assert.All(cards, c => Assert.True(Guid.TryParse(c.SentId, out _)));
    }

    [Fact]
    public async Task An_echo_finds_its_prompt_by_the_id_it_was_sent_with()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "first";
        await tab.SendCommand.ExecuteAsync(null);
        tab.ComposerText = "second";
        await tab.SendCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Count() == 2, "both prompts");
        var (first, second) = (tab.Items.OfType<UserMessageItem>().First(), tab.Items.OfType<UserMessageItem>().Last());

        // The second is echoed first, reading differently (Claude Code expanded a mention, say): its id still finds it.
        h.Transport.Emit(new JsonObject { ["type"] = "user", ["isReplay"] = true, ["uuid"] = second.SentId, ["message"] = new JsonObject { ["role"] = "user", ["content"] = "second, expanded" } }.ToJsonString());
        await TabTestHarness.Eventually(() => second.Uuid is not null, "the echo");
        Assert.Equal(second.SentId, second.Uuid);
        Assert.Null(first.Uuid);

        // Compact's /compact, the user's choice with no card of its own, is no prompt's echo.
        tab.ComposerText = "";
        await tab.CompactCommand.ExecuteAsync(null);
        var compact = h.Transport.Sent.Last(m => m["type"]?.GetValue<string>() == "user");
        Assert.Equal("human", compact["origin"]?["kind"]?.GetValue<string>());
        h.Transport.Emit(new JsonObject { ["type"] = "user", ["isReplay"] = true, ["uuid"] = compact["uuid"]!.GetValue<string>(), ["message"] = new JsonObject { ["role"] = "user", ["content"] = "/compact" } }.ToJsonString());
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Any(), "a turn");
        Assert.Null(first.Uuid);
    }

    [Fact]
    public async Task Send_now_sends_a_check_in_at_once_and_the_close_button_skips_one()
    {
        await using var h = new TabTestHarness();
        h.Services.Settings.CheckIns = new CheckInSettings { RunTimeMinutes = 0, QuietTimeMinutes = 5, Message = "Status?" };
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");

        Advance(h, TimeSpan.FromMinutes(5));
        await TabTestHarness.Eventually(() => tab.HasCheckInCountdown, "the countdown");
        tab.SkipCheckInCommand.Execute(null);

        await TabTestHarness.Eventually(() => !tab.HasCheckInCountdown, "the bar to close");
        Advance(h, CheckInMonitor.Countdown);
        Assert.DoesNotContain("Status?", h.Transport.SentUserTexts);
        Assert.DoesNotContain(tab.Items.OfType<UserMessageItem>(), m => m.IsCheckIn);

        // The next one comes a whole interval after the skip.
        Advance(h, TimeSpan.FromMinutes(5) - CheckInMonitor.Countdown);
        await TabTestHarness.Eventually(() => tab.HasCheckInCountdown, "the next countdown");
        tab.SendCheckInNowCommand.Execute(null);

        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains("Status?"), "the check-in");
        await TabTestHarness.Eventually(() => !tab.HasCheckInCountdown, "the bar to close");
    }

    private static void Advance(TabTestHarness h, TimeSpan by)
    {
        // In steps, so the check-in timer fires as it would.
        var end = h.Time.GetUtcNow() + by;
        while (h.Time.GetUtcNow() < end)
        {
            h.Time.Advance(CheckInMonitor.TickInterval);
        }
    }

    [Fact]
    public async Task Compact_sends_the_compact_command()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        await tab.CompactCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":1000}}""");

        Assert.Contains("/compact", h.Transport.SentUserTexts);
        await TabTestHarness.Eventually(() => tab.Items.OfType<NoteItem>().Any(n => n.Text == "Conversation compacted."), "the compacted note");
    }

    [Fact]
    public async Task Launch_uses_the_tab_overrides_over_the_defaults()
    {
        await using var h = new TabTestHarness();
        h.Services.Settings.NewTabs.DefaultModel = "sonnet";
        h.Services.Settings.NewTabs.DefaultEffort = "low";

        var tab = await h.OpenTabAsync();

        var launch = Assert.Single(h.Factory.Launches);
        Assert.Equal("sonnet", launch.Model);
        Assert.Equal("low", launch.Effort);
        Assert.Equal(h.WorkFolder, launch.WorkingDirectory);
        Assert.Equal("low", tab.Effort);
    }
}
