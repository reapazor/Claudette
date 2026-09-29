using Claudette.Core.Git;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Git;

public sealed class ProjectIdentityTests : IDisposable
{
    private const string Sha1 = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string Sha2 = "e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3";
    private const string Sha3 = "0123456789abcdef0123456789abcdef01234567";
    private const string Origin = "git@github.com:Owner/Repo.git";

    private readonly TempFolder _root = new("claudette-git");

    public void Dispose() => _root.Dispose();

    /// <summary>A repository made of files only: HEAD, config and a loose branch ref.</summary>
    private string CreateRepo(string name, string? remote = Origin, string branch = "main", string commit = Sha1)
    {
        var repo = _root.CreateFolder(name);
        _root.Write($"{name}/.git/HEAD", $"ref: refs/heads/{branch}\n");
        _root.Write($"{name}/.git/config", remote is null
            ? "[core]\n\tbare = false\n"
            : $"[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/someone-else/repo.git\n[remote \"origin\"]\n\turl = {remote}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        _root.Write($"{name}/.git/refs/heads/{branch}", commit + "\n");
        return repo;
    }

    [Theory]
    [InlineData("git@github.com:Owner/Repo.git")]
    [InlineData("https://user:token@github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/")]
    [InlineData("https://GitHub.com:443/Owner/Repo.git/")]
    [InlineData("git://github.com/owner/repo.git")]
    [InlineData("  https://github.com/owner/repo  ")]
    public void Remote_forms_normalize_to_host_and_path(string url)
    {
        Assert.Equal("github.com/owner/repo", ProjectIdentity.NormalizeRemote(url));
    }

    [Theory]
    [InlineData("ssh://git@gitlab.example.com:2222/Group/Sub/Repo.git", "gitlab.example.com/group/sub/repo")]
    [InlineData("git@gitlab.example.com:/srv/repo.git", "gitlab.example.com/srv/repo")]
    [InlineData("file:///C:/Repos/App.git", "c:/repos/app")]
    [InlineData("file:///srv/git/app.git", "/srv/git/app")]
    [InlineData(@"C:\Repos\App", "c:/repos/app")]
    [InlineData("/srv/git/App.git/", "/srv/git/app")]
    [InlineData("../app.git", "../app")]
    [InlineData("", "")]
    public void Other_remotes_and_local_paths_normalize(string url, string expected)
    {
        Assert.Equal(expected, ProjectIdentity.NormalizeRemote(url));
    }

    [Fact]
    public void Reads_remote_branch_commit_and_path_from_the_repo_files()
    {
        var repo = CreateRepo("repo");
        var sub = _root.CreateFolder("repo/src/api");

        Assert.Equal(new ProjectIdentity(Origin, "main", Sha1, ""), ProjectIdentity.Read(repo));
        Assert.Equal(new ProjectIdentity(Origin, "main", Sha1, "src/api"), ProjectIdentity.Read(sub));
    }

    [Fact]
    public void Without_origin_the_first_remote_is_used()
    {
        var repo = _root.CreateFolder("repo");
        _root.Write("repo/.git/HEAD", "ref: refs/heads/main\n");
        _root.Write("repo/.git/config", "[remote \"upstream\"]\n\turl = \"https://example.com/team/app.git\" ; a comment\n[remote \"fork\"]\n\turl = https://example.com/me/app.git\n");

        var identity = ProjectIdentity.Read(repo)!;

        Assert.Equal("https://example.com/team/app.git", identity.RemoteUrl);
        Assert.Null(identity.Commit);
    }

    [Fact]
    public void Without_a_remote_the_url_is_null()
    {
        var repo = CreateRepo("repo", remote: null);

        Assert.Null(ProjectIdentity.Read(repo)!.RemoteUrl);
    }

    [Fact]
    public void The_commit_comes_from_packed_refs_when_there_is_no_loose_ref()
    {
        var repo = CreateRepo("repo", branch: "feature/auth");
        File.Delete(_root.Combine("repo", ".git", "refs", "heads", "feature", "auth"));
        _root.Write("repo/.git/packed-refs", $"# pack-refs with: peeled fully-peeled sorted\n{Sha3} refs/heads/main\n{Sha2} refs/heads/feature/auth\n^{Sha1}\n");

        var identity = ProjectIdentity.Read(repo)!;

        Assert.Equal("feature/auth", identity.Branch);
        Assert.Equal(Sha2, identity.Commit);
    }

    [Fact]
    public void A_detached_head_has_a_commit_but_no_branch()
    {
        var repo = CreateRepo("repo");
        _root.Write("repo/.git/HEAD", Sha2.ToUpperInvariant() + "\n");

        var identity = ProjectIdentity.Read(repo)!;

        Assert.Null(identity.Branch);
        Assert.Equal(Sha2, identity.Commit);
    }

    [Fact]
    public void A_worktree_reads_its_own_head_and_the_common_config_and_refs()
    {
        CreateRepo("repo");
        _root.Write("repo/.git/refs/heads/feature/auth", Sha2 + "\n");
        _root.Write("repo/.git/worktrees/wt/HEAD", "ref: refs/heads/feature/auth\n");
        _root.Write("repo/.git/worktrees/wt/commondir", "../..\n");
        _root.Write("wt/.git", "gitdir: ../repo/.git/worktrees/wt\n");
        var sub = _root.CreateFolder("wt/sub");

        Assert.Equal(new ProjectIdentity(Origin, "feature/auth", Sha2, "sub"), ProjectIdentity.Read(sub));
    }

    [Fact]
    public void A_worktree_with_an_absolute_gitdir_and_a_packed_branch()
    {
        CreateRepo("repo");
        _root.Write("repo/.git/packed-refs", $"{Sha3} refs/heads/topic\n");
        _root.Write("repo/.git/worktrees/topic/HEAD", "ref: refs/heads/topic\n");
        _root.Write("repo/.git/worktrees/topic/commondir", "../..\n");
        _root.Write("topic/.git", $"gitdir: {_root.Combine("repo", ".git", "worktrees", "topic")}\n");

        Assert.Equal(new ProjectIdentity(Origin, "topic", Sha3, ""), ProjectIdentity.Read(_root.Combine("topic")));
    }

    [Fact]
    public void Outside_a_repository_there_is_no_identity()
    {
        Assert.SkipWhen(ProjectIdentity.Read(Path.GetTempPath()) is not null, "The temp folder is inside a git repository.");

        Assert.Null(ProjectIdentity.Read(_root.CreateFolder("plain/folder")));
    }

    [Fact]
    public void Finds_the_matching_folder_from_a_subfolder_candidate()
    {
        CreateRepo("elsewhere", remote: "https://github.com/other/thing.git");
        var repo = CreateRepo("clone", remote: "https://github.com/owner/repo");
        var api = _root.CreateFolder("clone/src/api");
        var docs = _root.CreateFolder("clone/docs");
        var recorded = new ProjectIdentity("git@github.com:Owner/Repo.git", "main", Sha1, "src/api");

        var found = ProjectIdentity.FindMatchingFolder(recorded, [_root.CreateFolder("not-a-repo"), _root.Combine("elsewhere"), _root.Combine("missing"), docs]);

        Assert.Equal(api, found);
        Assert.Equal(repo, ProjectIdentity.FindMatchingFolder(recorded with { PathInRepo = "" }, [docs]));
    }

    [Fact]
    public void No_match_when_the_path_is_missing_the_remote_differs_or_there_is_no_remote()
    {
        var repo = CreateRepo("clone", remote: "https://github.com/owner/repo");
        var recorded = new ProjectIdentity("https://github.com/owner/repo", "main", Sha1, "src/api");

        Assert.Null(ProjectIdentity.FindMatchingFolder(recorded, [repo]));
        Assert.Null(ProjectIdentity.FindMatchingFolder(recorded with { RemoteUrl = "https://github.com/owner/other", PathInRepo = "" }, [repo]));
        Assert.Null(ProjectIdentity.FindMatchingFolder(recorded with { RemoteUrl = null, PathInRepo = "" }, [repo]));
        Assert.Null(ProjectIdentity.FindMatchingFolder(recorded with { PathInRepo = "../elsewhere" }, [repo]));
    }

    [Fact]
    public void A_candidate_on_the_recorded_branch_is_preferred()
    {
        var develop = CreateRepo("develop-clone", branch: "develop");
        var main = CreateRepo("main-clone", branch: "main");
        var recorded = new ProjectIdentity(Origin, "main", Sha1, "");

        Assert.Equal(main, ProjectIdentity.FindMatchingFolder(recorded, [develop, main]));
        Assert.Equal(develop, ProjectIdentity.FindMatchingFolder(recorded with { Branch = "release" }, [develop, main]));
    }

    [Fact]
    public void Compare_reports_each_difference()
    {
        var recorded = new ProjectIdentity(Origin, "feature/auth", Sha1, "");

        Assert.Equal(new CodeDifference(false, false, false), ProjectIdentity.Compare(recorded, false, recorded));
        Assert.Equal(new CodeDifference(true, true, false), ProjectIdentity.Compare(recorded, false, recorded with { Branch = "main", Commit = Sha2 }));
        Assert.Equal(new CodeDifference(false, true, true), ProjectIdentity.Compare(recorded, true, recorded with { Commit = Sha2 }));
        Assert.Equal(new CodeDifference(false, false, false), ProjectIdentity.Compare(recorded, false, recorded with { Commit = Sha1.ToUpperInvariant(), PathInRepo = "x" }));
        Assert.Equal(new CodeDifference(true, true, false), ProjectIdentity.Compare(recorded, false, null));
    }

    [Fact]
    public void Describe_a_different_branch()
    {
        var recorded = new ProjectIdentity(Origin, "feature/auth", Sha1, "");
        var local = recorded with { Branch = "main", Commit = Sha2 };

        Assert.Equal(
            "This session was last used on DESKTOP-01 on branch `feature/auth` at `a1b2c3d`. This folder is on `main`. Claude's earlier file changes may not be here.",
            ProjectIdentity.Compare(recorded, false, local).Describe("DESKTOP-01", recorded, local));
    }

    [Fact]
    public void Describe_only_a_different_commit()
    {
        var recorded = new ProjectIdentity(Origin, "main", Sha1, "");
        var local = recorded with { Commit = Sha2 };

        Assert.Equal(
            "This session was last used on DESKTOP-01 on branch `main` at `a1b2c3d`. This folder is at `e4f5a6b`. Claude's earlier file changes may not be here.",
            ProjectIdentity.Compare(recorded, false, local).Describe("DESKTOP-01", recorded, local));
    }

    [Fact]
    public void Describe_uncommitted_changes_alone_and_with_a_different_branch()
    {
        var recorded = new ProjectIdentity(Origin, "main", Sha1, "");

        Assert.Equal(
            "This session was last used on DESKTOP-01 on branch `main` at `a1b2c3d`. DESKTOP-01 had uncommitted changes. Claude's earlier file changes may not be here.",
            ProjectIdentity.Compare(recorded, true, recorded).Describe("DESKTOP-01", recorded, recorded));

        var local = recorded with { Branch = "develop" };
        Assert.Equal(
            "This session was last used on DESKTOP-01 on branch `main` at `a1b2c3d`. This folder is on `develop`. DESKTOP-01 had uncommitted changes. Claude's earlier file changes may not be here.",
            ProjectIdentity.Compare(recorded, true, local).Describe("DESKTOP-01", recorded, local));
    }

    [Fact]
    public void Describe_a_detached_head_and_a_folder_outside_git()
    {
        var recorded = new ProjectIdentity(Origin, "main", Sha1, "");
        var detached = recorded with { Branch = null, Commit = Sha2 };

        Assert.Equal(
            "This session was last used on DESKTOP-01 on branch `main` at `a1b2c3d`. This folder is on a detached HEAD at `e4f5a6b`. Claude's earlier file changes may not be here.",
            ProjectIdentity.Compare(recorded, false, detached).Describe("DESKTOP-01", recorded, detached));
        Assert.Equal(
            "This session was last used on DESKTOP-01 on branch `main` at `a1b2c3d`. This folder isn't in a git repository. Claude's earlier file changes may not be here.",
            ProjectIdentity.Compare(recorded, false, null).Describe("DESKTOP-01", recorded, null));
        Assert.Equal(
            "This session was last used on DESKTOP-01 at `a1b2c3d`. This folder is on `main`. Claude's earlier file changes may not be here.",
            ProjectIdentity.Compare(detached with { Commit = Sha1 }, false, recorded).Describe("DESKTOP-01", detached with { Commit = Sha1 }, recorded));
    }

    [Fact]
    public void Describe_is_empty_when_nothing_differs()
    {
        var recorded = new ProjectIdentity(Origin, "main", Sha1, "");
        var difference = ProjectIdentity.Compare(recorded, false, recorded);

        Assert.False(difference.Any);
        Assert.Equal("", difference.Describe("DESKTOP-01", recorded, recorded));
    }
}
