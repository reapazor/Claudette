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
        Assert.Equal("120 tok", tab.TokensShort);
        Assert.Equal("s1", tab.State.SessionId);
        tab.IsSelected = true;
        Assert.Equal(TabStatus.Idle, tab.Status);
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
        Assert.True(tab.HasPendingModel);
        Assert.Contains("doesn't support high effort", tab.PendingModelMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("set_model", h.Transport.SentControlSubtypes);

        await tab.ConfirmModelSwitchCommand.ExecuteAsync(null);

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

        for (var i = 0; i < 31; i++)
        {
            h.Time.Advance(CheckInMonitor.TickInterval);
        }

        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains("Status?"), "the check-in");
        Assert.Contains(tab.Items.OfType<UserMessageItem>(), m => m.IsCheckIn);
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
