using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.ProjectTools;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>
/// The selected tab's project in the Settings window, rendered (DESIGN.md §14, "The project's pages"): the group below
/// the categories, and the Links page with its dialog.
/// </summary>
public class ProjectSettingsUiTests
{
    private static SettingsWindow ShowSettings(TabTestHarness h, TabViewModel tab, string? category, out SettingsViewModel settings)
    {
        settings = new SettingsViewModel(h.Services, "me@example.com", opening: new SettingsOpening(category, new ProjectSettingsViewModel(h.Services, tab, h.Shell)));
        var window = new SettingsWindow { DataContext = settings, Width = 900, Height = 700 };
        window.Show();
        UiText.Settle(window);
        return window;
    }

    private static async Task<TabViewModel> OpenTabAsync(TabTestHarness h, bool project)
    {
        var tab = await h.OpenTabAsync();
        if (project)
        {
            await Waiting.UntilAsync(() => tab.ProjectTools.Project is not null, "the project", poll: () => Avalonia.Threading.Dispatcher.UIThread.RunJobs());
        }
        Assert.Equal(project, tab.ProjectTools.Project is not null);
        return tab;
    }

    [AvaloniaFact]
    public async Task The_sidebar_ends_its_categories_with_the_tabs_project_and_its_pages_in_either_style()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        UnrealFixture.Write(h.Root, h.WorkFolder);
        var tab = await OpenTabAsync(h, project: true);
        var window = ShowSettings(h, tab, null, out var settings);

        var list = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "SidebarList");
        var group = list.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ProjectGroup");
        Assert.True(group.IsEffectivelyVisible);
        var heading = group.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(("NightOwl", h.WorkFolder), (heading.Text, ToolTip.GetTip(heading) as string));

        // One selection between the two lists: a project page, or a category.
        var categories = list.GetVisualDescendants().OfType<ListBox>().Single(l => AutomationProperties.GetName(l) == "Categories");
        var pages = group.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal("General", categories.SelectedItem);
        Assert.Null(pages.SelectedItem);
        pages.SelectedItem = SettingsViewModel.ToolsPage;
        UiText.Settle(window);
        Assert.True(settings.IsToolsPage);
        Assert.Null(categories.SelectedItem);
        var tools = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ToolsPage");
        var toolsText = UiText.Describe(tools, (h.Root, "{root}"));
        Assert.Contains("\"Engine folder\"", toolsText, StringComparison.Ordinal);
        Assert.Contains("[choice] \"Development\"", toolsText, StringComparison.Ordinal);
        categories.SelectedItem = "Appearance";
        UiText.Settle(window);
        Assert.True(settings.IsAppearance);
        Assert.Null(pages.SelectedItem);

        // The version foot stays at the bottom, below the list.
        var version = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Copy version details");
        var sidebar = version.FindAncestorOfType<DockPanel>()!;
        Assert.True(version.TranslatePoint(default, sidebar)!.Value.Y >= list.TranslatePoint(new Point(0, list.Bounds.Height), sidebar)!.Value.Y);

        // The Claude style, dark: the group is drawn with the app's own tokens, which the style repaints.
        var app = (App)Application.Current!;
        var before = app.RequestedThemeVariant;
        try
        {
            app.Colors.Apply(AppStyle.Claude);
            app.RequestedThemeVariant = ThemeVariant.Dark;
            UiText.Settle(window);
            var divider = group.GetVisualDescendants().OfType<Border>().First();
            Assert.Equal(((ISolidColorBrush)window.FindResource(ThemeVariant.Dark, "DividerBrush")!).Color, Assert.IsAssignableFrom<ISolidColorBrush>(divider.Background).Color);
            Assert.Equal(((ISolidColorBrush)window.FindResource(ThemeVariant.Dark, "MutedTextBrush")!).Color, Assert.IsAssignableFrom<ISolidColorBrush>(heading.Foreground).Color);
        }
        finally
        {
            app.Colors.Apply(AppStyle.Standard);
            app.RequestedThemeVariant = before;
            UiText.Settle(window);
        }

        // What the list holds; at this height it also scrolls, which the snapshot leaves out.
        var shown = UiText.Describe(Assert.IsType<StackPanel>(list.Content));
        window.Close();
        await Verify(shown);
    }

    [AvaloniaFact]
    public async Task With_no_tab_the_sidebar_has_no_project_group()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        using var settings = new SettingsViewModel(h.Services, null);
        var window = new SettingsWindow { DataContext = settings, Width = 900, Height = 700 };
        window.Show();
        UiText.Settle(window);

        var group = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ProjectGroup");
        Assert.False(group.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_Links_page_lists_both_files_links_and_its_dialog_checks_the_address()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        File.WriteAllText(Path.Combine(h.WorkFolder, ProjectFile.SharedName), """
            {
              "actions": [ { "name": "Run tests", "command": "make test" } ],
              "links": [
                { "name": "Board", "url": "https://example.atlassian.net/jira/software/projects/ABC/boards/1" },
                { "name": "Pull request", "url": "https://github.com/org/repo/compare/{branch}?expand=1" },
              ],
            }
            """);
        File.WriteAllText(Path.Combine(h.WorkFolder, ProjectFile.LocalName), """
            { "links": [ { "name": "Notes", "url": "file:///home/me/notes.txt" }, { "url": "mailto:me@example.com" } ] }
            """);
        var tab = await OpenTabAsync(h, project: false);
        var window = ShowSettings(h, tab, SettingsViewModel.LinksPage, out var settings);
        var page = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "LinksPage");
        Assert.True(page.IsEffectivelyVisible);
        var shown = UiText.Describe(page, (h.Root, "{root}"));

        settings.Project!.AddLinkCommand.Execute(null);
        UiText.Settle(window);
        var dialog = window.GetVisualDescendants().OfType<ProjectLinkEditorView>().Single();
        Assert.True(dialog.IsEffectivelyVisible);
        var address = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Address");
        address.Text = "javascript:alert(1)";
        UiText.Settle(window);
        var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save");
        Assert.False(save.IsEffectivelyEnabled);
        var dialogText = UiText.Describe(dialog, (h.Root, "{root}"));
        Assert.Contains("\"\"javascript:\" links don't open from Claudette; only https, http and mailto do.\"", dialogText, StringComparison.Ordinal);

        address.Text = "https://example.com/{folderName}";
        UiText.Settle(window);
        Assert.True(save.IsEffectivelyEnabled);
        save.Command!.Execute(null);
        UiText.Settle(window);
        Assert.False(settings.Project.HasEditor);
        Assert.Equal("https://example.com/{folderName}", settings.Project.Links[^1].Url);

        window.Close();
        await Verify($"{shown}\n--- Add a link, with an address that won't open ---\n{dialogText}");
    }

    [AvaloniaFact]
    public async Task The_action_dialog_asks_what_must_exist_and_scrolls_to_Save_in_a_window_at_its_opening_size()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await OpenTabAsync(h, project: false);
        var window = ShowSettings(h, tab, SettingsViewModel.ActionsPage, out var settings);
        window.Width = 820;
        window.Height = 650;

        // From the project's menu: the tallest the dialog gets, as it asks for the file too.
        settings.Project!.StartNewAction();
        UiText.Settle(window);
        var dialog = window.GetVisualDescendants().OfType<ProjectActionEditorView>().Single();
        Assert.True(dialog.IsEffectivelyVisible);
        // Nothing is cut off: its title is in the window at the top, and Save once scrolled to the end.
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "EditorScroller");
        var title = dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Add an action");
        Assert.InRange(title.TranslatePoint(default, window)!.Value.Y, 0, window.ClientSize.Height);
        scroller.ScrollToEnd();
        UiText.Settle(window);
        var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save");
        Assert.InRange(save.TranslatePoint(new Point(0, save.Bounds.Height), window)!.Value.Y, 0, window.ClientSize.Height);
        var ifExists = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Only show when this exists");
        Assert.Equal("Always shown", ifExists.PlaceholderText);

        h.Platform.FileToPick = Path.Combine(h.WorkFolder, "Build", "Game.exe");
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Browse…").Command!.Execute(null);
        UiText.Settle(window);
        Assert.Equal("Build/Game.exe", ifExists.Text);

        window.Close();
    }
}
