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
/// Project tools rendered (DESIGN.md §18): the project's row at the sidebar's foot and its menu, with the folder's
/// links, and the runs under a tab's row in the sidebar.
/// </summary>
public class ProjectToolsUiTests
{
    [AvaloniaFact]
    public async Task A_tab_in_an_Unreal_project_shows_the_project_at_the_sidebars_foot_and_its_menu_lists_the_actions()
    {
        await using var h = new TabTestHarness(settings => settings.ProjectTools.ProjectFileFormat = ProjectFileFormat.VisualStudio, dispatcher: new AvaloniaUiDispatcher());
        UnrealFixture.Write(h.Root, h.WorkFolder);
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        await UiText.SettleUntilAsync(window, () => tab.Project is not null, "the project");

        // The project's row sits at the sidebar's foot, for the selected tab; the composer's bar has no project chip.
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Project tools");
        Assert.Equal("ProjectButton", button.Name);
        Assert.True(button.IsEffectivelyVisible);
        Assert.Equal("[button] Project tools: NightOwl · UE 5.4", UiText.Describe(button).Trim());
        Assert.Null(button.FindAncestorOfType<TabView>());

        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        UiText.Settle(window);
        var menu = Assert.IsAssignableFrom<Control>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => menu.IsEffectivelyVisible, "the menu");
        var shown = UiText.Describe(menu, (h.Root, "{root}"));
        var openSolution = menu.GetVisualDescendants().OfType<Button>().Single(b => UiText.Describe(b).Contains("Open solution", StringComparison.Ordinal));
        Assert.False(openSolution.IsEffectivelyEnabled);
        Assert.Equal("Generate project files first", ToolTip.GetTip(openSolution));
        flyout.Hide();

        // The tab's menu leaves the project to the row: it has no project entries.
        var row = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tab") && b.DataContext == tab);
        var headers = row.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Contains("Tab settings…", headers);
        Assert.DoesNotContain("NightOwl (UE 5.4)", headers);
        Assert.DoesNotContain("Add an action…", headers);

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
    public async Task A_folder_with_no_project_has_the_row_named_after_it_and_its_menu_lists_the_links()
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
        await UiText.SettleUntilAsync(window, () => tab.HasLinks, "the links");

        // No provider recognized a project, and there are no actions: the row is there all the same, named after the folder.
        Assert.Null(tab.Project);
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Project tools");
        Assert.True(button.IsEffectivelyVisible);
        Assert.Equal($"[button] Project tools: {tab.FolderName}", UiText.Describe(button).Trim());
        Assert.StartsWith(tab.Folder, ToolTip.GetTip(button) as string, StringComparison.Ordinal);

        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        UiText.Settle(window);
        var menu = Assert.IsAssignableFrom<Control>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => menu.IsEffectivelyVisible, "the menu");
        var shown = UiText.Describe(menu, (h.WorkFolder, "{folder}"), (tab.FolderName, "{folderName}"));
        var buttons = menu.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        var board = buttons.Single(b => UiText.Describe(b).Contains("Board", StringComparison.Ordinal));
        Assert.True(board.IsEffectivelyEnabled);
        Assert.Equal("https://example.atlassian.net/jira/software/projects/ABC/boards/1", ToolTip.GetTip(board));
        var pullRequest = buttons.Single(b => UiText.Describe(b).Contains("Pull request", StringComparison.Ordinal));
        Assert.False(pullRequest.IsEffectivelyEnabled);
        Assert.Equal("No git branch", ToolTip.GetTip(pullRequest));
        // No Project page without project tools, so nothing to show the output of.
        Assert.DoesNotContain(buttons, b => UiText.Describe(b).Contains("Show output…", StringComparison.Ordinal));
        flyout.Hide();

        await Verify(shown);
    }
}
