using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>
/// Project tools rendered (DESIGN.md §18): the chip in the composer bar, its menu, the tab menu's submenu, the Links
/// section, and the runs under a tab's row in the sidebar.
/// </summary>
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
    public async Task A_tabs_runs_are_listed_under_its_row_with_Stop_while_running_and_close_once_done()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher(), launcher: launcher);
        UnrealFixture.Write(h.Root, h.WorkFolder);
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        await UiText.SettleUntilAsync(window, () => tab.Project is not null, "the project");
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Classes.Contains("runs"));
        Assert.False(list.IsEffectivelyVisible);

        // One that failed, then one that's running.
        await tab.RunProjectActionCommand.ExecuteAsync(tab.ProjectActions.Single(a => a.Id == "generate-project-files"));
        launcher.Processes.Last().WriteOutput("Generating...");
        launcher.Processes.Last().Exit(6);
        var failed = tab.ProjectRuns.Single();
        await UiText.SettleUntilAsync(window, () => failed.Failed, "the failure");
        await tab.RunProjectActionCommand.ExecuteAsync(tab.ProjectActions.Single(a => a.Id == "build-editor"));
        var running = tab.ProjectRuns[^1];
        UiText.Settle(window);

        Assert.True(list.IsEffectivelyVisible);
        var shown = UiText.Describe(list);
        var rows = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("runrow")).ToList();
        Assert.Equal([failed, running], rows.Select(r => r.DataContext));
        // Stop takes the close button's place while a job runs, so a stray click never ends a build.
        static Button Visible(Button row, string name) =>
            row.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == name);
        Assert.DoesNotContain(rows[1].GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Close");
        Assert.Equal("Stop: ends the job and every process it started", ToolTip.GetTip(Visible(rows[1], "Stop")));
        Assert.Equal("Build editor is running…\nStarted 12:00.", ToolTip.GetTip(rows[1]));
        // The detail, under the name, is muted, or in the error color for a failure.
        TextBlock Detail(Button row) => row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("rundetail"));
        Assert.True(Application.Current!.TryGetResource("MutedTextBrush", window.ActualThemeVariant, out var muted));
        Assert.True(Application.Current.TryGetResource("ErrorTextBrush", window.ActualThemeVariant, out var error));
        Assert.Equal(((ISolidColorBrush)muted!).Color, ((ISolidColorBrush)Detail(rows[1]).Foreground!).Color);
        Assert.Equal(((ISolidColorBrush)error!).Color, ((ISolidColorBrush)Detail(rows[0]).Foreground!).Color);

        // A click on an entry shows its log on the Project page.
        rows[0].Command!.Execute(null);
        UiText.Settle(window);
        Assert.True(failed.IsShowing);
        Assert.Contains("showing", rows[0].Classes);
        var output = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ProjectOutputList");
        Assert.True(output.IsEffectivelyVisible);
        Assert.Same(failed.Output, output.ItemsSource);

        // Stop, and the running one becomes a closable entry; closing takes the entry away.
        Visible(rows[1], "Stop").Command!.Execute(null);
        await UiText.SettleUntilAsync(window, () => running.IsStopped, "the stop");
        Visible(rows[1], "Close").Command!.Execute(null);
        UiText.Settle(window);
        Assert.Equal([failed], list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("runrow")).Select(r => r.DataContext));

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
