using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>Project tools rendered (DESIGN.md §18): the chip in the composer bar, its menu, and the tab menu's submenu.</summary>
public class ProjectToolsUiTests
{
    [AvaloniaFact]
    public async Task A_tab_in_an_Unreal_project_shows_the_project_chip_and_its_menu_lists_the_actions()
    {
        await using var h = new TabTestHarness(settings => settings.ProjectTools.ProjectFileFormat = ProjectFileFormat.VisualStudio, dispatcher: new AvaloniaUiDispatcher());
        UnrealFixture.Write(h.Root, h.WorkFolder);
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        await UiText.SettleUntilAsync(window, () => tab.Project is not null, "the project");

        var chip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ProjectChip");
        Assert.True(chip.IsEffectivelyVisible);
        Assert.Equal("Project tools", AutomationProperties.GetName(chip));
        Assert.Contains("\"NightOwl · UE 5.4 ▾\"", UiText.Describe(chip), StringComparison.Ordinal);

        var flyout = Assert.IsType<Flyout>(chip.Flyout);
        flyout.ShowAt(chip);
        UiText.Settle(window);
        var menu = Assert.IsAssignableFrom<Control>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => menu.IsEffectivelyVisible, "the menu");
        var shown = UiText.Describe(menu, (h.Root, "{root}"));
        var openSolution = menu.GetVisualDescendants().OfType<Button>().Single(b => UiText.Describe(b).Contains("Open solution", StringComparison.Ordinal));
        Assert.False(openSolution.IsEffectivelyEnabled);
        Assert.Equal("Generate project files first", ToolTip.GetTip(openSolution));
        flyout.Hide();

        // The tab's menu in the sidebar has the same entries, in a submenu named after the project.
        var row = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tab") && b.DataContext == tab);
        var contextMenu = row.ContextMenu!;
        contextMenu.Open(row);
        UiText.Settle(window);
        var submenu = contextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "NightOwl (UE 5.4)");
        Assert.True(submenu.IsVisible);
        Assert.False(contextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Add an action…").IsVisible);
        submenu.IsSubMenuOpen = true;
        UiText.Settle(window);
        var items = Enumerable.Range(0, submenu.ItemCount).Select(i => submenu.ContainerFromIndex(i)).OfType<MenuItem>().ToList();
        Assert.Equal(tab.ProjectMenu.Count, items.Count);
        Assert.Equal("Launch editor", items[0].Header);
        Assert.True(items[0].IsEffectivelyEnabled);
        Assert.False(items.Single(i => i.Header as string == "Open solution").IsEnabled);
        Assert.Equal(MenuItemToggleType.Radio, items.Single(i => i.Header as string == "DebugGame").ToggleType);
        Assert.Contains(items, i => i.Header as string == "-");
        contextMenu.Close();

        // Verify resumes off the UI thread, so it comes last.
        await Verify(shown);
    }

    [AvaloniaFact]
    public async Task The_Links_section_shows_the_selected_tabs_links_and_follows_the_selection()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        File.WriteAllText(Path.Combine(h.WorkFolder, Core.ProjectTools.ProjectFile.SharedName), """
            { "links": [
                { "name": "Board", "url": "https://example.atlassian.net/jira/software/projects/ABC/boards/1" },
                { "name": "Pull request", "url": "https://github.com/org/repo/compare/{branch}?expand=1" },
            ] }
            """);
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        await UiText.SettleUntilAsync(window, () => h.Shell.HasLinks, "the links");
        var section = window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "LinksSection");

        Assert.True(section.IsEffectivelyVisible);
        var shown = UiText.Describe(section);
        var buttons = section.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        var board = buttons.Single(b => UiText.Describe(b).Contains("Board", StringComparison.Ordinal));
        Assert.True(board.IsEffectivelyEnabled);
        Assert.Equal("https://example.atlassian.net/jira/software/projects/ABC/boards/1", ToolTip.GetTip(board));
        var pullRequest = buttons.Single(b => UiText.Describe(b).Contains("Pull request", StringComparison.Ordinal));
        Assert.False(pullRequest.IsEffectivelyEnabled);
        Assert.Equal("No git branch", ToolTip.GetTip(pullRequest));

        // Another tab, whose folder has no links (and is gone, so it starts no session): the section goes.
        var other = new TabViewModel(h.Services, h.Shell, new TabState { Folder = Path.Combine(h.Root, "elsewhere") }, isRestored: false);
        var group = new TabGroupViewModel(other.Folder, 1, isCollapsed: false);
        group.Tabs.Add(other);
        h.Shell.Groups.Add(group);
        h.Shell.SelectedTab = other;
        UiText.Settle(window);
        Assert.False(section.IsEffectivelyVisible);

        h.Shell.SelectedTab = tab;
        await UiText.SettleUntilAsync(window, () => section.IsEffectivelyVisible, "the links again");

        await Verify(shown);
    }
}
