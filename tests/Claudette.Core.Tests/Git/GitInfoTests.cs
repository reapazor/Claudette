using Claudette.Core.Git;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Git;

/// <summary>The branch read from the <c>.git</c> folder alone (the tab info card, the new-tab picker).</summary>
public sealed class GitInfoTests : IDisposable
{
    private readonly TempFolder _folder = new();

    private string Root => _folder.Path;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Reads_the_git_branch_including_from_a_subfolder_and_a_worktree()
    {
        var repo = Path.Combine(Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "deep"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/feature/auth\n");
        var worktreeGit = Path.Combine(Root, "wt-git");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "HEAD"), "0123456789abcdef\n");
        var worktree = Path.Combine(Root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}\n");

        Assert.Equal("feature/auth", GitInfo.TryGetBranch(Path.Combine(repo, "src", "deep")));
        Assert.Equal("0123456", GitInfo.TryGetBranch(worktree));
    }

    [Fact]
    public void A_folder_outside_a_repository_has_no_branch_and_another_ref_is_named_in_full()
    {
        Assert.Null(GitInfo.TryGetBranch(Root));
        var repo = Path.Combine(Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/remotes/origin/main\n");

        Assert.Equal("refs/remotes/origin/main", GitInfo.TryGetBranch(repo));
    }
}
