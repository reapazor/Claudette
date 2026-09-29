using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>Finding a tab folder's projects: in it, below it and above it, within bounds (DESIGN.md §18, "Project tools").</summary>
public sealed class ProjectDiscoveryTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-discovery");
    private readonly UnrealProvider _unreal = new();

    public void Dispose() => _temp.Dispose();

    private IReadOnlyList<string> Find(string relativeFolder, string? p4Config = null)
    {
        _temp.CreateFolder(relativeFolder);
        var context = ProjectToolFixtures.Context(_temp);
        context = new ProjectToolContext
        {
            OS = context.OS,
            Settings = context.Settings,
            Paths = context.Paths,
            P4ConfigName = p4Config,
        };
        return _unreal.Find(_temp.Combine(relativeFolder.Split('/')), context).Select(c => Path.GetRelativePath(_temp.Path, c.Path).Replace('\\', '/')).ToArray();
    }

    [Fact]
    public void A_uproject_in_the_tab_folder_is_found()
    {
        _temp.CreateFolder("repo/.git");
        ProjectToolFixtures.UProject(_temp, "repo/NightOwl.uproject");

        var found = Assert.Single(_unreal.Find(_temp.Combine("repo"), ProjectToolFixtures.Context(_temp)));

        Assert.Equal("NightOwl", found.Name);
        Assert.Equal(UnrealProvider.KindId, found.Kind);
    }

    [Fact]
    public void Subfolders_are_searched_two_levels_down_but_not_three()
    {
        _temp.CreateFolder("repo/.git");
        ProjectToolFixtures.UProject(_temp, "repo/Game/NightOwl/NightOwl.uproject");
        ProjectToolFixtures.UProject(_temp, "repo/Deep/Er/Still/TooDeep.uproject");

        Assert.Equal(["repo/Game/NightOwl/NightOwl.uproject"], Find("repo"));
    }

    [Fact]
    public void Build_output_caches_and_version_control_folders_are_never_searched()
    {
        _temp.CreateFolder("repo/.git");
        foreach (var skipped in (string[])["Intermediate", "Saved", "DerivedDataCache", "Binaries", ".git", "node_modules", "Engine", "Templates", ".hidden"])
        {
            ProjectToolFixtures.UProject(_temp, $"repo/{skipped}/Inside.uproject");
        }
        ProjectToolFixtures.UProject(_temp, "repo/Games/Kept.uproject");

        Assert.Equal(["repo/Games/Kept.uproject"], Find("repo"));
    }

    [Fact]
    public void A_tab_opened_on_Source_finds_the_project_above_it()
    {
        _temp.CreateFolder("repo/.git");
        ProjectToolFixtures.UProject(_temp, "repo/NightOwl/NightOwl.uproject");

        Assert.Equal(["repo/NightOwl/NightOwl.uproject"], Find("repo/NightOwl/Source/NightOwl/Private"));
    }

    [Fact]
    public void The_parent_walk_stops_at_the_repository_root()
    {
        ProjectToolFixtures.UProject(_temp, "Outside.uproject");
        _temp.CreateFolder("repo/.git");
        ProjectToolFixtures.UProject(_temp, "repo/Inside.uproject");

        Assert.Equal(["repo/Inside.uproject"], Find("repo/Source/Game"));
        // A git worktree's .git is a file.
        _temp.Write("wt/.git", "gitdir: ../repo/.git/worktrees/wt");
        _temp.CreateFolder("wt/Source");
        Assert.Empty(Find("wt/Source"));
    }

    [Fact]
    public void A_Perforce_config_file_marks_the_root_too()
    {
        ProjectToolFixtures.UProject(_temp, "Outside.uproject");
        _temp.Write("depot/.p4config", "P4CLIENT=matt-ws");
        _temp.CreateFolder("depot/Source");
        _temp.Write("stream/p4config.txt", "P4CLIENT=matt-ws");
        _temp.CreateFolder("stream/Source");

        Assert.Empty(Find("depot/Source"));
        Assert.Empty(Find("stream/Source", p4Config: "p4config.txt"));
        Assert.Equal(["Outside.uproject"], Find("stream/Source"));
    }

    [Fact]
    public void Without_a_repository_the_parent_walk_stops_after_six_levels()
    {
        ProjectToolFixtures.UProject(_temp, "a/Far.uproject");
        ProjectToolFixtures.UProject(_temp, "a/b/Near.uproject");

        Assert.Equal(["a/b/Near.uproject"], Find("a/b/c/d/e/f/g/h"));
        Assert.Equal(["a/b/Near.uproject", "a/Far.uproject"], Find("a/b/c/d/e/f/g"));
    }

    [Fact]
    public void Several_projects_come_nearest_first()
    {
        _temp.CreateFolder("repo/.git");
        ProjectToolFixtures.UProject(_temp, "repo/Tools/Tool.uproject");
        ProjectToolFixtures.UProject(_temp, "repo/Main.uproject");
        ProjectToolFixtures.UProject(_temp, "repo/Games/Beta/Beta.uproject");
        ProjectToolFixtures.UProject(_temp, "repo/Games/Alpha/Alpha.uproject");

        Assert.Equal(["repo/Main.uproject", "repo/Tools/Tool.uproject", "repo/Games/Alpha/Alpha.uproject", "repo/Games/Beta/Beta.uproject"], Find("repo"));
    }

    [Fact]
    public void The_detector_uses_the_pick_remembered_for_the_folder_else_the_nearest()
    {
        _temp.CreateFolder("repo/.git");
        ProjectToolFixtures.UProject(_temp, "repo/Main.uproject");
        var tool = ProjectToolFixtures.UProject(_temp, "repo/Tools/Tool.uproject");
        var detector = new ProjectToolDetector([_unreal]);
        var context = ProjectToolFixtures.Context(_temp);

        Assert.Equal("Main", detector.Detect(_temp.Combine("repo"), context).Project!.Name);
        var picked = detector.Detect(_temp.Combine("repo"), context, chosenPath: tool);

        Assert.Equal(2, picked.Candidates.Count);
        Assert.Equal("Tool", picked.Project!.Name);
        Assert.Same(ProjectDetection.None, detector.Detect(_temp.Combine("nothing-here"), context));
    }
}
