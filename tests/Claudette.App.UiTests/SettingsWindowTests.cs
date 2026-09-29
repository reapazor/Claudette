using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
