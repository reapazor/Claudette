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
}
