using Claudette.Core.ScratchPads;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ScratchPads;

/// <summary>Which scratch pad a folder's tabs share (DESIGN.md §18, "Scratch pad").</summary>
public sealed class ScratchPadProjectTests : IDisposable
{
    private const string Commit = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private readonly TempFolder _root = new("claudette-pad");

    public void Dispose() => _root.Dispose();

    /// <summary>A repository made of files only: HEAD, config and a branch.</summary>
    private string CreateRepo(string name, string? remote)
    {
        var repo = _root.CreateFolder(name);
        _root.Write($"{name}/.git/HEAD", "ref: refs/heads/main\n");
        _root.Write($"{name}/.git/config", remote is null ? "[core]\n\tbare = false\n" : $"[core]\n\tbare = false\n[remote \"origin\"]\n\turl = {remote}\n");
        _root.Write($"{name}/.git/refs/heads/main", Commit + "\n");
        return repo;
    }

    /// <summary>A linked worktree of <paramref name="repo"/>, as <c>git worktree add</c> lays it out.</summary>
    private string CreateWorktree(string repo, string name)
    {
        var folder = _root.CreateFolder(name);
        var gitDir = Path.Combine(repo, ".git", "worktrees", name);
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(folder, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/worktree-" + name + "\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        return folder;
    }

    [Fact]
    public void Clones_of_one_remote_share_a_pad_however_the_remote_is_written()
    {
        var ssh = ScratchPadProject.For(CreateRepo("here", "git@github.com:Owner/Repo.git"));
        var https = ScratchPadProject.For(CreateRepo("there", "https://github.com/owner/repo"));

        Assert.Equal(ssh.Id, https.Id);
        Assert.True(ssh.IsShared);
        Assert.Equal("github.com/owner/repo", ssh.Remote);
        Assert.Matches("^[0-9a-f]{16}$", ssh.Id);
    }

    [Fact]
    public void A_worktree_shares_its_checkouts_pad()
    {
        var repo = CreateRepo("repo", "git@github.com:owner/repo.git");
        var worktree = CreateWorktree(repo, "brisk-otter");

        Assert.Equal(ScratchPadProject.For(repo).Id, ScratchPadProject.For(worktree).Id);
    }

    [Fact]
    public void A_folder_inside_the_repository_has_a_pad_of_its_own()
    {
        var repo = CreateRepo("repo", "git@github.com:owner/repo.git");
        var web = _root.CreateFolder("repo/web");

        var project = ScratchPadProject.For(web);

        Assert.NotEqual(ScratchPadProject.For(repo).Id, project.Id);
        Assert.Equal("web", project.PathInRepo);
        Assert.True(project.IsShared);
    }

    [Fact]
    public void Without_a_remote_the_pad_stays_on_this_machine_and_worktrees_still_share_it()
    {
        var repo = CreateRepo("repo", remote: null);
        var worktree = CreateWorktree(repo, "brisk-otter");

        var project = ScratchPadProject.For(repo);

        Assert.False(project.IsShared);
        Assert.True(project.IsInRepository);
        Assert.Null(project.Remote);
        Assert.Equal(project.Id, ScratchPadProject.For(worktree).Id);
        Assert.NotEqual(project.Id, ScratchPadProject.For(CreateRepo("other", remote: null)).Id);
    }

    [Fact]
    public void A_folder_outside_git_has_its_own_pad_on_this_machine()
    {
        var folder = _root.CreateFolder("notes");

        var project = ScratchPadProject.For(folder);

        Assert.False(project.IsShared);
        Assert.False(project.IsInRepository);
        Assert.Equal(project.Id, ScratchPadProject.For(folder + Path.DirectorySeparatorChar).Id);
        Assert.NotEqual(project.Id, ScratchPadProject.For(_root.CreateFolder("other")).Id);
    }
}
