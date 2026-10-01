using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>The Settings window rendered headlessly, a category at a time (DESIGN.md §14).</summary>
public class SettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData("General")]
    [InlineData("New tabs")]
    [InlineData("Appearance")]
    [InlineData("Sessions")]
    public async Task A_category_shows_its_settings_and_Reset_to_defaults(string category)
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var favorite = Directory.CreateDirectory(Path.Combine(h.Root, "projects-favorite", "api")).FullName;
        h.Services.State.FavoriteFolders.Add(favorite);
        var settings = new SettingsViewModel(h.Services, "me@example.com") { SelectedCategory = category };
        var window = new SettingsWindow { DataContext = settings, Width = 900, Height = 700 };
        window.Show();
        UiText.Settle(window);

        var page = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => Grid.GetColumn(s) == 1);

        await Verify(UiText.Describe(page, (h.Root, "{root}"), (Environment.MachineName, "{machine}"))).UseParameters(category);
    }

    /// <summary>
    /// The search box's entries name what their pages show (DESIGN.md §14): each page, rendered, has the words of each
    /// of its entries (<see cref="SettingsSearchResult.PageText"/>, or else the label), so a setting reworded or removed
    /// on its page can't leave the index pointing at nothing. Text a page holds while hidden counts, since some settings
    /// only show in some states, such as a custom diff tool's command or an update to install.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_search_entry_names_something_its_page_shows()
    {
        await using var h = new TabTestHarness(updater: new FakeClaudeUpdater(new Version(2, 1, 284)), dispatcher: new AvaloniaUiDispatcher());
        UnrealFixture.Write(h.Root, h.WorkFolder);
        var tab = await h.OpenTabAsync();
        await Waiting.UntilAsync(() => tab.ProjectTools.Project is not null, "the project", poll: () => Dispatcher.UIThread.RunJobs());
        var updates = new ClaudeUpdateViewModel(h.Services, h.Services.ClaudeUpdates!, () => h.Shell.RunningVersions);
        await h.Services.ClaudeUpdates!.CheckNowAsync();
        using var settings = new SettingsViewModel(h.Services, "me@example.com", updates, new SettingsOpening(Project: new ProjectSettingsViewModel(h.Services, tab, h.Shell)));
        var window = new SettingsWindow { DataContext = settings, Width = 900, Height = 700 };
        window.Show();

        var missing = new List<string>();
        foreach (var page in settings.Pages)
        {
            settings.SelectedCategory = page.Title;
            UiText.Settle(window);
            var view = window.GetVisualDescendants().OfType<UserControl>().First(v => ReferenceEquals(v.DataContext, page));
            var shown = TextsOf(view);
            missing.AddRange(page.SearchEntries
                .Where(entry => !shown.Any(text => text.Contains(entry.PageText ?? entry.Label, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => $"{entry.Where}: {entry.Label}"));
        }
        window.Close();

        Assert.Equal([.. SettingsViewModel.AllCategories, .. SettingsViewModel.ProjectPages], settings.Pages.Select(p => p.Title));
        Assert.Empty(missing);
    }

    /// <summary>The words in a view, shown or hidden: its text blocks', and its buttons' and check boxes' labels.</summary>
    private static List<string> TextsOf(Control view) =>
    [
        .. view.GetLogicalDescendants().Cast<object>().Concat(view.GetVisualDescendants()).Distinct().Select(element => element switch
        {
            TextBlock block => block.Text,
            ContentControl { Content: string content } => content,
            _ => null,
        }).OfType<string>(),
    ];

    /// <summary>
    /// Typing in a text setting doesn't save and apply it on every keystroke, which refreshed every tab each time; it
    /// takes effect when the box loses focus.
    /// </summary>
    [AvaloniaFact]
    public async Task A_text_setting_takes_effect_when_the_box_loses_focus_not_on_each_keystroke()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var settings = new SettingsViewModel(h.Services, null) { SelectedCategory = "Advanced" };
        var window = new SettingsWindow { DataContext = settings, Width = 900, Height = 700 };
        window.Show();
        UiText.Settle(window);
        var changes = 0;
        h.Services.SettingsChanged += (_, _) => changes++;
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "for example --add-dir ../shared");

        box.Focus();
        window.KeyTextInput("--verbose");
        UiText.Settle(window);
        Assert.Equal(0, changes);

        window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Search settings").Focus();
        UiText.Settle(window);
        Assert.Equal(1, changes);
        Assert.Equal("--verbose", settings.Advanced.ExtraArguments);
    }

    /// <summary>
    /// A setting's label beside its control is never cut off at the window's usual size: a long one wraps (GitHub issue
    /// #7, "Refresh the panel every (seconds)").
    /// </summary>
    [AvaloniaFact]
    public async Task No_settings_label_is_cut_off()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var settings = new SettingsViewModel(h.Services, null);
        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        var cut = new List<string>();
        foreach (var category in SettingsViewModel.AllCategories)
        {
            settings.SelectedCategory = category;
            UiText.Settle(window);
            cut.AddRange(window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("label") && t.IsEffectivelyVisible)
                .Where(t => t.TextLayout.Width > t.Bounds.Width + 0.5)
                .Select(t => $"{category}: {t.Text}"));
        }

        Assert.Empty(cut);
    }

    [AvaloniaFact]
    public async Task The_sidebar_ends_with_the_version_and_Report_an_issue()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.Services.BuildCommit = "842169b";
        h.Services.IsSourceBuild = true;
        var settings = new SettingsViewModel(h.Services, null);
        var window = new SettingsWindow { DataContext = settings, Width = 900, Height = 700 };
        window.Show();
        UiText.Settle(window);

        var version = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Copy version details");
        var sidebar = version.FindAncestorOfType<DockPanel>()!;
        Assert.True(version.IsEffectivelyVisible);
        Assert.Equal("Claudette 0.1.0 · 842169b", version.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        var report = sidebar.GetVisualDescendants().OfType<Button>().Single(b => b.Command == settings.ReportIssueCommand);
        Assert.True(report.IsEffectivelyVisible);
        // The foot sits below the categories, not over them, with Report an issue last.
        var categories = sidebar.GetVisualDescendants().OfType<ListBox>().First(l => l.IsVisible);
        var categoriesBottom = categories.TranslatePoint(new Point(0, categories.Bounds.Height), sidebar)!.Value.Y;
        Assert.True(version.TranslatePoint(default, sidebar)!.Value.Y >= categoriesBottom);
        Assert.True(report.TranslatePoint(default, sidebar)!.Value.Y >= version.TranslatePoint(new Point(0, version.Bounds.Height), sidebar)!.Value.Y);

        version.Command!.Execute(null);
        UiText.Settle(window);

        Assert.Equal("Copied", version.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.StartsWith("Claudette: 0.1.0 (source build 842169b)", h.Platform.Clipboard, StringComparison.Ordinal);
    }
}
