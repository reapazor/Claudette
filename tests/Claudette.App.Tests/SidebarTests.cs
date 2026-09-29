using Avalonia.Input;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Protocol;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>The sidebar that lists the tabs: collapsing to its rail, resizing, and its rows (DESIGN.md §3, §4).</summary>
public class SidebarTests
{
    [Fact]
    public async Task Collapsing_the_sidebar_is_remembered()
    {
        await using var h = new TabTestHarness();
        Assert.False(h.Shell.IsSidebarCollapsed);
        Assert.Equal(ShellViewModel.DefaultSidebarWidth, h.Shell.SidebarDisplayWidth);

        h.Shell.ToggleSidebarCommand.Execute(null);

        Assert.True(h.Shell.IsSidebarCollapsed);
        Assert.Equal(ShellViewModel.RailWidth, h.Shell.SidebarDisplayWidth);
        Assert.True(h.Services.State.SidebarCollapsed);
        Assert.True(new ShellViewModel(h.Services, () => { }).IsSidebarCollapsed);
    }

    [Fact]
    public async Task A_narrow_window_shows_the_rail_without_changing_the_users_choice()
    {
        await using var h = new TabTestHarness();
        h.Shell.SetAvailableWidth(1200);

        h.Shell.SetAvailableWidth(ShellViewModel.NarrowWidth - 1);
        Assert.True(h.Shell.IsSidebarCollapsed);
        Assert.False(h.Services.State.SidebarCollapsed);

        // Expanding while narrow is only for now.
        h.Shell.ToggleSidebarCommand.Execute(null);
        Assert.False(h.Shell.IsSidebarCollapsed);
        Assert.False(h.Services.State.SidebarCollapsed);

        h.Shell.SetAvailableWidth(1200);
        Assert.False(h.Shell.IsSidebarCollapsed);

        // Collapsed by the user, it stays collapsed after the window has been narrow.
        h.Shell.ToggleSidebarCommand.Execute(null);
        h.Shell.SetAvailableWidth(600);
        h.Shell.SetAvailableWidth(1200);
        Assert.True(h.Shell.IsSidebarCollapsed);
    }

    [Fact]
    public async Task Resizing_the_sidebar_is_kept_within_limits_and_saved()
    {
        await using var h = new TabTestHarness();

        h.Shell.ResizeSidebar(40);
        Assert.Equal(ShellViewModel.MinSidebarWidth, h.Shell.SidebarWidth);
        h.Shell.ResizeSidebar(2000);
        Assert.Equal(ShellViewModel.MaxSidebarWidth, h.Shell.SidebarWidth);

        h.Shell.ResizeSidebar(300);
        Assert.Null(h.Services.State.SidebarWidth);
        h.Shell.SaveSidebarWidth();

        Assert.Equal(300, h.Services.State.SidebarWidth);
        Assert.Equal(300, new ShellViewModel(h.Services, () => { }).SidebarDisplayWidth);
    }

    [Fact]
    public async Task Move_up_and_down_keep_pinned_tabs_first()
    {
        await using var h = new TabTestHarness();
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true }, new TabState { Folder = h.WorkFolder }, new TabState { Folder = h.WorkFolder }];
        h.Shell.Restore(null);
        var group = h.Shell.Groups.Single();
        var (a, b, c) = (group.Tabs[0], group.Tabs[1], group.Tabs[2]);

        h.Shell.MoveTabUpCommand.Execute(c);
        Assert.Equal([a, c, b], group.Tabs);

        h.Shell.MoveTabUpCommand.Execute(c);
        h.Shell.MoveTabDownCommand.Execute(a);
        Assert.Equal([a, c, b], group.Tabs);

        h.Shell.MoveTabDownCommand.Execute(c);
        Assert.Equal([a, b, c], group.Tabs);
    }

    [Fact]
    public async Task A_row_shows_the_model_or_what_needs_attention()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(tab.ModelBadge, tab.RowDetail);

        tab.Status = TabStatus.NeedsInput;
        Assert.Equal("Needs your input", tab.RowDetail);
        tab.Status = TabStatus.Error;
        Assert.Equal(tab.StatusTip, tab.RowDetail);
        tab.Status = TabStatus.Working;
        Assert.Equal(tab.ModelBadge, tab.RowDetail);

        tab.Effort = "high";
        Assert.EndsWith("High", tab.RowDetail, StringComparison.Ordinal);
        Assert.Contains(nameof(TabViewModel.RowDetail), changed);

        // Check-ins that got no reply (DESIGN.md §5).
        changed.Clear();
        tab.IsPossiblyStuck = true;
        Assert.Equal("Possibly stuck", tab.RowDetail);
        Assert.Contains(nameof(TabViewModel.RowDetail), changed);
    }

    [Fact]
    public async Task A_failed_tab_shows_the_error_on_its_row()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Exit(1, "Loading settings…\nError: Invalid model name: claude-nonsense\n");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Error, "the error");

        Assert.Equal("Error: Invalid model name: claude-nonsense", tab.RowDetail);
        Assert.Contains(tab.InfoRows, r => r.Label == "Status" && r.Value == "Error: Invalid model name: claude-nonsense");
        Assert.Equal("Claude Code stopped unexpectedly (exit code 3)", TabViewModel.ExitErrorMessage(new TransportExit(3, "  \n")));
    }

    [Fact]
    public async Task The_sidebar_has_a_shortcut_named_in_its_tooltips()
    {
        await using var h = new TabTestHarness();
        var primary = Shortcuts.IsMac ? KeyModifiers.Meta : KeyModifiers.Control;
        var shown = Shortcuts.IsMac ? "⌘B" : "Ctrl+B";

        Assert.True(Shortcuts.Matches(h.Services.Settings.Keyboard, KeyboardShortcuts.ToggleSidebar, Key.B, primary));
        Assert.Equal($"Collapse the sidebar ({shown})", h.Shell.Tips.CollapseSidebar);
        Assert.Equal($"Expand the sidebar ({shown})", h.Shell.Tips.ExpandSidebar);
    }

    [Theory]
    [InlineData("refactor the auth middleware", "R")]
    [InlineData("  #42 flaky test", "4")]
    [InlineData("", "·")]
    [InlineData(null, "·")]
    public void A_collapsed_row_shows_the_names_first_letter(string? name, string expected) =>
        Assert.Equal(expected, Converters.Initials(name));
}
