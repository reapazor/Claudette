using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Moving around by keyboard: prompt recall, the next tab waiting for you, the command palette (DESIGN.md §4, §5).</summary>
public sealed class KeyboardFlowTests
{
    private static async Task SendAsync(TabTestHarness h, TabViewModel tab, string text)
    {
        tab.ComposerText = text;
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the turn");
    }

    [Fact]
    public async Task Up_and_Down_go_through_earlier_prompts_and_back_to_what_was_typed()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        await SendAsync(h, tab, "one");
        await SendAsync(h, tab, "two");
        await SendAsync(h, tab, "two");
        tab.ComposerText = "draft";

        Assert.True(tab.RecallOlderPrompt());
        Assert.Equal("two", tab.ComposerText);
        // The same prompt twice in a row is kept once.
        Assert.True(tab.RecallOlderPrompt());
        Assert.Equal("one", tab.ComposerText);
        Assert.False(tab.RecallOlderPrompt());
        Assert.Equal("one", tab.ComposerText);

        Assert.True(tab.RecallNewerPrompt());
        Assert.Equal("two", tab.ComposerText);
        Assert.True(tab.RecallNewerPrompt());
        Assert.Equal("draft", tab.ComposerText);
        Assert.False(tab.IsRecallingPrompt);
        Assert.False(tab.RecallNewerPrompt());

        // Typing ends it: the next Up starts from the newest again.
        Assert.True(tab.RecallOlderPrompt());
        tab.ComposerText = "two, but different";
        Assert.False(tab.IsRecallingPrompt);
        Assert.True(tab.RecallOlderPrompt());
        Assert.Equal("two", tab.ComposerText);
    }

    [Fact]
    public async Task A_restored_tabs_prompts_can_be_recalled()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("s1", """{"type":"user","uuid":"u1","timestamp":"2026-09-28T10:00:00Z","message":{"role":"user","content":"from before"}}""");
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "s1" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the restored tab");

        Assert.True(tab.RecallOlderPrompt());
        Assert.Equal("from before", tab.ComposerText);
    }

    [Fact]
    public async Task The_next_tab_waiting_for_you_is_found_in_sidebar_order_after_the_selected_one()
    {
        await using var h = new TabTestHarness();
        h.Factory.ProcessPerSession = true;
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true }, new TabState { Folder = h.WorkFolder }, new TabState { Folder = h.WorkFolder }];
        h.Shell.Restore(null);
        var group = h.Shell.Groups.Single();
        var (a, b, c) = (group.Tabs[0], group.Tabs[1], group.Tabs[2]);
        Assert.Same(a, h.Shell.SelectedTab);

        h.Shell.SelectNextNeedingInputCommand.Execute(null);
        Assert.Same(a, h.Shell.SelectedTab);

        c.Status = TabStatus.NeedsInput;
        h.Shell.SelectNextNeedingInputCommand.Execute(null);
        Assert.Same(c, h.Shell.SelectedTab);

        // From the last tab it goes round to the first.
        b.Status = TabStatus.NeedsInput;
        h.Shell.SelectNextNeedingInputCommand.Execute(null);
        Assert.Same(b, h.Shell.SelectedTab);
    }

    [Fact]
    public async Task The_command_palette_lists_commands_tabs_folders_and_settings_and_runs_one()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Shell.OpenPaletteCommand.Execute(null);
        var palette = h.Shell.Palette!;

        Assert.Contains(palette.Results, e => e is { Label: "New tab", Group: "Command", Shortcut: "Ctrl+T" or "⌘T" });
        Assert.Contains(palette.Results, e => e.Group == "Tab" && e.Label == tab.DisplayName);
        Assert.Contains(palette.Results, e => e.Group == "Folder" && e.Detail == tab.Folder);
        Assert.Contains(palette.Results, e => e is { Group: "Settings", Label: SettingsCategory.Keyboard });
        // Only what applies now: Claude isn't working, and no other tab is waiting.
        Assert.DoesNotContain(palette.Results, e => e.Label is "Stop Claude" or "Go to the next tab waiting for you");

        palette.Query = "find in";
        Assert.Equal("Find in the conversation", palette.Selected!.Label);
        await palette.RunCommand.ExecuteAsync(null);

        Assert.Null(h.Shell.Palette);
        Assert.True(tab.Find.IsOpen);
    }
}
