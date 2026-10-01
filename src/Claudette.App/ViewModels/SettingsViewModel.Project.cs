using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// Where the Settings window opens (DESIGN.md §14): a category or one of the project pages, the project pages of the
/// tab that was selected, and whether to start a new entry on that page straight away (<b>Add an action…</b> on
/// Actions, <b>Add a link…</b> on Links).
/// </summary>
/// <param name="Project">The selected tab's pages; null with no tab open, and the group is hidden.</param>
public sealed record SettingsOpening(string? Category = null, ProjectSettingsViewModel? Project = null, bool StartNew = false);

/// <summary>
/// The group below the categories in Settings' sidebar (DESIGN.md §14, "The project's pages"): the selected tab's
/// project, with its Links, Actions and Tools pages (<see cref="Settings.ProjectPage"/>). The window follows the tab
/// that was selected when it opened.
/// </summary>
public sealed partial class SettingsViewModel
{
    public const string LinksPage = "Links";

    public const string ActionsPage = "Actions";

    public const string ToolsPage = "Tools";

    /// <summary>The project group's pages, in the sidebar's order.</summary>
    public static readonly IReadOnlyList<string> ProjectPages = [LinksPage, ActionsPage, ToolsPage];

    /// <summary>The selected tab's project pages; null with no tab open.</summary>
    public ProjectSettingsViewModel? Project { get; }

    public bool HasProject => Project is not null;

    /// <summary>The categories list's selection: null while a project page shows, so only one list has a selection.</summary>
    public string? SelectedMainCategory
    {
        get => AllCategories.Contains(SelectedCategory) ? SelectedCategory : null;
        set
        {
            // The list clears its selection when a project page is picked; that isn't a choice.
            if (value is not null && AllCategories.Contains(value))
            {
                SelectedCategory = value;
            }
        }
    }

    /// <summary>The project group's selection: null while one of the categories shows.</summary>
    public string? SelectedProjectPage
    {
        get => HasProject && ProjectPages.Contains(SelectedCategory) ? SelectedCategory : null;
        set
        {
            if (value is not null && HasProject && ProjectPages.Contains(value))
            {
                SelectedCategory = value;
            }
        }
    }

    /// <summary>From the Tools page: the app-wide defaults are in Settings → Project tools.</summary>
    [RelayCommand]
    private void ShowProjectToolsDefaults() => SelectedCategory = SettingsCategory.ProjectTools;
}
