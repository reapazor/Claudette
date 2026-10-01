namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// One of the selected tab's project pages, below the categories (DESIGN.md §14, "The project's pages"). The pages
/// show <see cref="Project"/>, which holds what they edit; their search entries are named with the project's group
/// ("NightOwl → Links"), worked out as the search runs, since the project can be found after the window opens.
/// </summary>
public abstract class ProjectPage(SettingsContext context, string title, ProjectSettingsViewModel project) : SettingsPage(context, title)
{
    public ProjectSettingsViewModel Project { get; } = project;

    protected override SettingsSearchResult Entry(string label, string? pageText = null) => base.Entry(label, pageText) with { Group = Project.Heading };

    /// <summary>The project's pages have no <b>Reset to defaults</b>: its files and remembered choices aren't settings.</summary>
    protected sealed override void ResetSettings()
    {
    }
}

/// <summary>The project's <b>Links</b> (DESIGN.md §18, "Links"), from both of its files.</summary>
public sealed class ProjectLinksPage(SettingsContext context, ProjectSettingsViewModel project) : ProjectPage(context, SettingsViewModel.LinksPage, project)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Web links for this project"),
        Entry("Add a link", pageText: "Add…"),
    ];
}

/// <summary>The project's <b>Actions</b> (DESIGN.md §18, "Custom actions"), the editor that was in Tab settings….</summary>
public sealed class ProjectActionsPage(SettingsContext context, ProjectSettingsViewModel project) : ProjectPage(context, SettingsViewModel.ActionsPage, project)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Project actions: commands of your own", pageText: "Commands of your own"),
        Entry("Add an action", pageText: "Add…"),
    ];
}

/// <summary>
/// The project's <b>Tools</b> page: this machine's choices for it, as the project menus make them (DESIGN.md §18). Its
/// entries depend on the project's kind: "Engine folder", "Editor configuration".
/// </summary>
public sealed class ProjectToolChoicesPage(SettingsContext context, ProjectSettingsViewModel project) : ProjectPage(context, SettingsViewModel.ToolsPage, project)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("This project's engine, editor or Godot executable", pageText: "Choose…"),
        .. Project.ToolsSearchLabels.Select(label => Entry(label)),
    ];
}
