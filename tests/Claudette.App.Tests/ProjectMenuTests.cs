using Claudette.App.ViewModels;
using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.Tests;

/// <summary>
/// The menu of the project's row at the sidebar's foot (DESIGN.md §18, "Project tools"), built from what the tab knows
/// about its project, without a tab.
/// </summary>
public class ProjectMenuTests
{
    private static readonly ProjectMenuCommands Commands = new(
        new RelayCommand(() => { }), new RelayCommand(() => { }), new RelayCommand(() => { }), new RelayCommand(() => { }),
        new RelayCommand(() => { }), new RelayCommand(() => { }), new RelayCommand(() => { }), new RelayCommand(() => { }),
        new RelayCommand(() => { }));

    private static readonly ProjectAction Build = new("build", "Build editor", ProjectActionKind.Run) { Description = "Builds the editor" };

    private static readonly ProjectAction Launch = new("launch", "Launch editor", ProjectActionKind.Launch);

    private static readonly ProjectAction Tests = new("custom:tests", "Run tests", ProjectActionKind.Run);

    private static readonly ProjectChoice Configuration = new("configuration", "Editor configuration",
        [new ProjectChoiceOption("Development", "Development"), new ProjectChoiceOption("DebugGame", "DebugGame")], "DebugGame");

    private static readonly ProjectFix ChooseEngine = new("engine", "Choose engine folder…", "Where is Unreal Engine?", PickFolder: true, path => (path, null));

    private static ProjectInfo Project(string path = "/work/NightOwl/NightOwl.uproject") => new()
    {
        Kind = "unreal",
        KindName = "Unreal Engine",
        Name = "NightOwl",
        Root = "/work/NightOwl",
        ProjectPath = path,
        Actions = [Build, Launch],
        Choice = Configuration,
        Fix = ChooseEngine,
    };

    private static IReadOnlyList<ProjectMenuEntry> Menu(
        ProjectInfo? project = null, IReadOnlyList<ProjectCandidate>? candidates = null, IReadOnlyList<ProjectAction>? custom = null,
        IReadOnlyList<ResolvedLink>? links = null, bool hasTools = false, Func<ProjectAction, ProjectAction>? forJob = null) =>
        ProjectMenu.Build(project, candidates ?? [], custom ?? [], links ?? [], hasTools, forJob ?? (a => a), Commands);

    private static string[] Labels(IEnumerable<ProjectMenuEntry> menu) => menu.Select(e => e.IsSeparator ? "-" : e.Label).ToArray();

    [Fact]
    public void Without_a_project_the_menu_has_only_its_own_commands()
    {
        var menu = Menu();

        Assert.Equal(["Add an action…", "Add a link…", "Refresh"], Labels(menu));
        Assert.All(menu, e => Assert.Equal(ProjectMenuKind.Command, e.Kind));
        Assert.Equal([Commands.AddAction, Commands.AddLink, Commands.Refresh], menu.Select(e => e.Command));
        Assert.Equal(["A command of your own for this folder", "A web page of your own for this folder", "Look for the project and its files again"], menu.Select(e => e.Tip));
    }

    [Fact]
    public void The_projects_actions_choice_and_fix_then_the_folders_own_and_its_links_come_between_separators()
    {
        var docs = new ResolvedLink("Docs", "https://example.com/docs", null, ProjectFileScope.Shared);
        var local = new ResolvedLink("Local file", null, "Only https, http and mailto links open.", ProjectFileScope.Local);

        var menu = Menu(Project(), custom: [Tests], links: [docs, local], hasTools: true);

        Assert.Equal(
        [
            "Build editor", "Launch editor",
            "-", "Editor configuration", "Development", "DebugGame",
            "-", "Choose engine folder…",
            "-", "Run tests",
            "-", "Links", "Docs", "Local file",
            "-", "Show output…", "Add an action…", "Add a link…", "Refresh",
        ], Labels(menu));

        var build = menu.Single(e => e.Label == "Build editor");
        Assert.Equal(ProjectMenuKind.Action, build.Kind);
        Assert.Same(Commands.RunAction, build.Command);
        Assert.Same(Build, build.Parameter);
        Assert.Equal("Builds the editor", build.Tip);
        Assert.Same(Tests, menu.Single(e => e.Label == "Run tests").Parameter);

        Assert.True(menu.Single(e => e.Label == "Editor configuration").IsHeader);
        var (development, debugGame) = (menu.Single(e => e.Label == "Development"), menu.Single(e => e.Label == "DebugGame"));
        Assert.True(debugGame.IsOption);
        Assert.True(debugGame.IsChecked);
        Assert.Equal("●", debugGame.Mark);
        Assert.False(development.IsChecked);
        Assert.Equal("○", development.Mark);
        Assert.Same(Commands.ChooseOption, debugGame.Command);
        Assert.Same(Configuration.Options[1], debugGame.Parameter);

        var fix = menu.Single(e => e.Label == "Choose engine folder…");
        Assert.Equal(ProjectMenuKind.Command, fix.Kind);
        Assert.Equal("Where is Unreal Engine?", fix.Tip);
        Assert.Same(Commands.Fix, fix.Command);

        var (docsEntry, localEntry) = (menu.Single(e => e.Label == "Docs"), menu.Single(e => e.Label == "Local file"));
        Assert.True(docsEntry.IsLink);
        Assert.True(docsEntry.IsEnabled);
        Assert.Equal("https://example.com/docs", docsEntry.Tip);
        Assert.Equal("↗", docsEntry.Mark);
        Assert.Same(Commands.OpenLink, docsEntry.Command);
        Assert.Same(docs, docsEntry.Parameter);
        Assert.False(localEntry.IsEnabled);
        Assert.Equal("Only https, http and mailto links open.", localEntry.Tip);

        Assert.Same(Commands.ShowOutput, menu.Single(e => e.Label == "Show output…").Command);
    }

    [Fact]
    public void A_folder_with_several_projects_offers_them_with_the_one_in_use_checked()
    {
        var game = new ProjectCandidate("unreal", "/work/NightOwl/NightOwl.uproject", "NightOwl");
        var tool = new ProjectCandidate("godot", "/work/NightOwl/Tools/project.godot", "Level tool");

        var menu = Menu(Project(game.Path), candidates: [game, tool], hasTools: true);

        Assert.Equal(["Projects in this folder", "NightOwl", "Level tool", "-", "Build editor"], Labels(menu).Take(5));
        var (gameEntry, toolEntry) = (menu.Single(e => e.Label == "NightOwl"), menu.Single(e => e.Label == "Level tool"));
        Assert.True(gameEntry.IsChecked);
        Assert.False(toolEntry.IsChecked);
        Assert.Equal(tool.Path, toolEntry.Tip);
        Assert.Same(Commands.ChooseProject, toolEntry.Command);
        Assert.Same(tool, toolEntry.Parameter);

        // A single project is no choice.
        Assert.DoesNotContain("Projects in this folder", Labels(Menu(Project(game.Path), candidates: [game])));
    }

    [Fact]
    public void Separators_only_fall_between_groups_that_are_there()
    {
        var candidates = new[] { new ProjectCandidate("unreal", "/work/a/A.uproject", "A"), new ProjectCandidate("unreal", "/work/b/B.uproject", "B") };

        // None of the projects could be read: no actions, choice or fix between the choice of project and the menu's own.
        Assert.Equal(["Projects in this folder", "A", "B", "-", "Add an action…", "Add a link…", "Refresh"], Labels(Menu(candidates: candidates)));
        // Only the folder's own actions: no separator before them.
        Assert.Equal(["Run tests", "-", "Show output…", "Add an action…", "Add a link…", "Refresh"], Labels(Menu(custom: [Tests], hasTools: true)));
    }

    [Fact]
    public void Actions_show_as_the_running_job_leaves_them()
    {
        static ProjectAction WhileBuilding(ProjectAction action) =>
            action.Kind == ProjectActionKind.Run ? action with { DisabledReason = "Build editor is still running." } : action;

        var menu = Menu(Project(), custom: [Tests], forJob: WhileBuilding);

        var (build, launch, tests) = (menu.Single(e => e.Label == "Build editor"), menu.Single(e => e.Label == "Launch editor"), menu.Single(e => e.Label == "Run tests"));
        Assert.False(build.IsEnabled);
        Assert.Equal("Build editor is still running.", build.Tip);
        Assert.False(Assert.IsType<ProjectAction>(build.Parameter).IsEnabled);
        Assert.False(tests.IsEnabled);
        Assert.True(launch.IsEnabled);
        Assert.Same(Launch, launch.Parameter);
    }
}
