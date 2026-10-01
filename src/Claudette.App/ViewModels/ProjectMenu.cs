using System.Windows.Input;
using Claudette.Core.ProjectTools;

namespace Claudette.App.ViewModels;

/// <summary>What an entry of the project's menu is.</summary>
public enum ProjectMenuKind
{
    /// <summary>A label, such as "Editor configuration".</summary>
    Header,
    /// <summary>A project action, such as Launch editor.</summary>
    Action,
    /// <summary>One of a set of choices, such as DebugGame: a radio item.</summary>
    Option,
    Separator,
    /// <summary>Something about the menu itself: Show output…, Add an action…, Add a link…, Refresh.</summary>
    Command,
    /// <summary>A link from the folder's project files, opened in the browser.</summary>
    Link,
}

/// <summary>One entry of the menu of the project's row at the sidebar's foot (DESIGN.md §18, "Project tools").</summary>
public sealed class ProjectMenuEntry
{
    public required ProjectMenuKind Kind { get; init; }

    public string Label { get; init; } = "";

    public string? Tip { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsChecked { get; init; }

    public ICommand? Command { get; init; }

    public object? Parameter { get; init; }

    public bool IsSeparator => Kind == ProjectMenuKind.Separator;

    public bool IsHeader => Kind == ProjectMenuKind.Header;

    public bool IsOption => Kind == ProjectMenuKind.Option;

    /// <summary>A button in the project's menu: an action, a choice or a command.</summary>
    public bool IsButton => Kind is ProjectMenuKind.Action or ProjectMenuKind.Option or ProjectMenuKind.Command or ProjectMenuKind.Link;

    public bool IsLink => Kind == ProjectMenuKind.Link;

    /// <summary>A radio mark for choices in the project's menu, and an arrow for links.</summary>
    public string Mark => Kind switch
    {
        ProjectMenuKind.Option => IsChecked ? "●" : "○",
        ProjectMenuKind.Link => "↗",
        _ => "",
    };

    public bool HasMark => Kind is ProjectMenuKind.Option or ProjectMenuKind.Link;

    public override string ToString() => Label;
}

/// <summary>The commands the project's menu entries run, each with the entry's <see cref="ProjectMenuEntry.Parameter"/>.</summary>
/// <param name="RunAction">An action, with the <see cref="ProjectAction"/>.</param>
/// <param name="ChooseProject">One of several projects in the folder, with the <see cref="ProjectCandidate"/>.</param>
/// <param name="ChooseOption">A per-project choice, with the <see cref="ProjectChoiceOption"/>.</param>
/// <param name="Fix">The project's fix, such as <b>Choose engine folder…</b>.</param>
/// <param name="OpenLink">A link, with the <see cref="ResolvedLink"/>.</param>
/// <param name="ShowOutput"><b>Show output…</b>: the Project page of the side panel.</param>
/// <param name="AddAction"><b>Add an action…</b>.</param>
/// <param name="AddLink"><b>Add a link…</b>.</param>
/// <param name="Refresh"><b>Refresh</b>: look for the project and its files again.</param>
internal sealed record ProjectMenuCommands(
    ICommand RunAction, ICommand ChooseProject, ICommand ChooseOption, ICommand Fix, ICommand OpenLink,
    ICommand ShowOutput, ICommand AddAction, ICommand AddLink, ICommand Refresh);

/// <summary>
/// Builds the menu of the project's row at the sidebar's foot (DESIGN.md §18, "Project tools"): the projects to choose
/// from when the folder has several, the project's actions, its choice and its fix, the folder's own actions, its
/// links, then what the menu itself offers. Separators fall only between groups that are there.
/// </summary>
internal static class ProjectMenu
{
    /// <param name="project">The project in use; null when none was recognized.</param>
    /// <param name="candidates">Every project found for the folder: with more than one, the menu offers a choice.</param>
    /// <param name="customActions">The folder's own actions, from its project files.</param>
    /// <param name="links">The links of the folder's project files, filled in for the tab.</param>
    /// <param name="hasTools">The tab has a Project page, so the menu offers <b>Show output…</b>.</param>
    /// <param name="forJob">How an action shows as things are: while a job runs, other jobs wait.</param>
    public static IReadOnlyList<ProjectMenuEntry> Build(
        ProjectInfo? project, IReadOnlyList<ProjectCandidate> candidates, IReadOnlyList<ProjectAction> customActions,
        IReadOnlyList<ResolvedLink> links, bool hasTools, Func<ProjectAction, ProjectAction> forJob, ProjectMenuCommands commands)
    {
        var entries = new List<ProjectMenuEntry>();
        void Separator()
        {
            if (entries.Count > 0 && !entries[^1].IsSeparator)
            {
                entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Separator });
            }
        }
        ProjectMenuEntry ActionEntry(ProjectAction action) => new()
        {
            Kind = ProjectMenuKind.Action,
            Label = action.Label,
            Tip = action.Tip,
            IsEnabled = action.IsEnabled,
            Command = commands.RunAction,
            Parameter = action,
        };

        if (candidates.Count > 1)
        {
            entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Header, Label = "Projects in this folder" });
            foreach (var candidate in candidates)
            {
                entries.Add(new ProjectMenuEntry
                {
                    Kind = ProjectMenuKind.Option,
                    Label = candidate.Name,
                    Tip = candidate.Path,
                    IsChecked = project is { } current && current.ProjectPath == candidate.Path,
                    Command = commands.ChooseProject,
                    Parameter = candidate,
                });
            }
            Separator();
        }
        if (project is not null)
        {
            entries.AddRange(project.Actions.Select(a => ActionEntry(forJob(a))));
            if (project.Choice is { } choice)
            {
                Separator();
                entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Header, Label = choice.Label });
                foreach (var option in choice.Options)
                {
                    entries.Add(new ProjectMenuEntry
                    {
                        Kind = ProjectMenuKind.Option,
                        Label = option.Label,
                        IsChecked = option.Value == choice.Selected,
                        Command = commands.ChooseOption,
                        Parameter = option,
                    });
                }
            }
            if (project.Fix is { } fix)
            {
                Separator();
                entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = fix.Label, Tip = fix.Title, Command = commands.Fix });
            }
        }
        if (customActions.Count > 0)
        {
            Separator();
            entries.AddRange(customActions.Select(a => ActionEntry(forJob(a))));
        }
        if (links.Count > 0)
        {
            Separator();
            entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Header, Label = "Links" });
            entries.AddRange(links.Select(link => new ProjectMenuEntry
            {
                Kind = ProjectMenuKind.Link,
                Label = link.Name,
                Tip = link.Tip,
                IsEnabled = link.IsEnabled,
                Command = commands.OpenLink,
                Parameter = link,
            }));
        }
        Separator();
        // Without project tools there's no Project page to show, and no job to have output.
        if (hasTools)
        {
            entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Show output…", Tip = "The Project page of the side panel", Command = commands.ShowOutput });
        }
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Add an action…", Tip = "A command of your own for this folder", Command = commands.AddAction });
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Add a link…", Tip = "A web page of your own for this folder", Command = commands.AddLink });
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Refresh", Tip = "Look for the project and its files again", Command = commands.Refresh });
        return entries;
    }
}
