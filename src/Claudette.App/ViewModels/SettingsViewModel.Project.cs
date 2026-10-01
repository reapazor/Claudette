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
/// project, with its Links, Actions and Tools pages. The window follows the tab that was selected when it opened.
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

    public bool IsLinksPage => HasProject && SelectedCategory == LinksPage;

    public bool IsActionsPage => HasProject && SelectedCategory == ActionsPage;

    public bool IsToolsPage => HasProject && SelectedCategory == ToolsPage;

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsPerforce));
        OnPropertyChanged(nameof(IsLinksPage));
        OnPropertyChanged(nameof(IsActionsPage));
        OnPropertyChanged(nameof(IsToolsPage));
        OnPropertyChanged(nameof(SelectedMainCategory));
        OnPropertyChanged(nameof(SelectedProjectPage));
    }

    /// <summary>From the Tools page: the app-wide defaults are in Settings → Project tools.</summary>
    [RelayCommand]
    private void ShowProjectToolsDefaults() => SelectedCategory = SettingsCategory.ProjectTools;

    /// <summary>
    /// The project pages' entries for the search box, named with the project's group ("NightOwl → Links"). The Tools
    /// page's depend on the project's kind, so they're worked out as the search runs.
    /// </summary>
    private IEnumerable<SettingsSearchResult> ProjectSearchEntries()
    {
        if (Project is not { } project)
        {
            return [];
        }
        var group = project.Heading;
        return
        [
            new(LinksPage, "Web links for this project") { Group = group },
            new(LinksPage, "Add a link") { Group = group },
            new(ActionsPage, "Project actions: commands of your own") { Group = group },
            new(ActionsPage, "Add an action") { Group = group },
            new(ToolsPage, "This project's engine, editor or Godot executable") { Group = group },
            .. project.ToolsSearchLabels.Select(label => new SettingsSearchResult(ToolsPage, label) { Group = group }),
        ];
    }
}
