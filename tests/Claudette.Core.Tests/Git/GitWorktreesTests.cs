using Claudette.Core.Git;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Git;

/// <summary>
/// The worktrees of worktree tabs (DESIGN.md §4, "Worktree tabs"), against the real <c>git</c> where it's needed, isolated
/// from the user's and system git config.
/// </summary>
public sealed class GitWorktreesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-worktrees-{Guid.NewGuid():N}");
    private readonly string _repo;
    private readonly IsolatedGitLauncher _launcher;
    private readonly GitWorktrees _worktrees;

    public GitWorktreesTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        _launcher = new IsolatedGitLauncher(_root);
        _worktrees = new GitWorktrees(new GitWorkingTree(_launcher, TimeProvider.System));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_list_reads_each_worktree_its_branch_and_lock()
    {
        var porcelain = string.Join('\0',
            "worktree /src/app", "HEAD 1111111", "branch refs/heads/main", "",
            "worktree /src/app/.claude/worktrees/brisk-otter", "HEAD 2222222", "branch refs/heads/worktree-brisk-otter", "locked claude session brisk-otter (pid 4)", "",
            "worktree /src/app/.claude/worktrees/detached", "HEAD 3333333", "detached", "", "");

        var list = GitWorktrees.ParseList(porcelain);

        Assert.Equal(3, list.Count);
        Assert.True(list[0].IsMain);
        Assert.Equal("main", list[0].Branch);
        Assert.Equal(("worktree-brisk-otter", true, false), (list[1].Branch, list[1].IsLocked, list[1].IsMain));
        Assert.Equal(Path.GetFullPath("/src/app/.claude/worktrees/brisk-otter"), list[1].Path);
        Assert.Null(list[2].Branch);
    }

    [Fact]
    public void New_names_are_two_words_and_avoid_taken_ones()
    {
        var taken = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var name = GitWorktrees.NewName(new Random(3), taken.Contains);
            Assert.Matches("^[a-z]+-[a-z]+(-[0-9]+)?$", name);
            Assert.True(taken.Add(name));
        }
    }

    [Fact]
    public void A_worktree_Claude_Code_made_belongs_to_the_checkout_it_is_in()
    {
        var main = Path.Combine(_root, "app");
        Assert.Equal(main, GitWorktrees.MainCheckoutOf(GitWorktrees.PathFor(main, "brisk-otter")));
        Assert.Equal(main, GitWorktrees.MainCheckoutOf(GitWorktrees.PathFor(main, "brisk-otter") + Path.DirectorySeparatorChar));
        Assert.Null(GitWorktrees.MainCheckoutOf(main));
        Assert.Null(GitWorktrees.MainCheckoutOf(Path.Combine(main, "worktrees", "brisk-otter")));
    }

    [Fact]
    public async Task An_empty_worktree_is_removed_with_its_branch_even_when_locked()
    {
        Assert.SkipWhen(!IsolatedGitLauncher.GitInstalled, "git isn't on PATH.");
        var path = await AddWorktreeAsync("brisk-otter", locked: true);

        var worktree = Assert.IsType<GitWorktree>(await _worktrees.FindAsync(_repo, path, Token));
        Assert.True(worktree.IsLocked);
        Assert.Equal("worktree-brisk-otter", worktree.Branch);
        Assert.Equal(new GitWorktreeWork(false, 0), await _worktrees.InspectAsync(worktree, Token));

        Assert.Null(await _worktrees.RemoveAsync(_repo, worktree, discard: false, ownCommits: 0, Token));

        Assert.False(Directory.Exists(path));
        Assert.Null(await _worktrees.FindAsync(_repo, path, Token));
        Assert.DoesNotContain("worktree-brisk-otter", await BranchesAsync());
    }

    [Fact]
    public async Task Work_in_a_worktree_is_counted_and_kept_unless_discarded()
    {
        Assert.SkipWhen(!IsolatedGitLauncher.GitInstalled, "git isn't on PATH.");
        var path = await AddWorktreeAsync("calm-heron", locked: false);
        await _launcher.RunAsync(path, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "own");
        await File.WriteAllTextAsync(Path.Combine(path, "notes.md"), "draft\n", Token);
        var worktree = Assert.IsType<GitWorktree>(await _worktrees.FindAsync(_repo, path, Token));

        Assert.Equal(new GitWorktreeWork(true, 1), await _worktrees.InspectAsync(worktree, Token));
        // Without discarding, git keeps a worktree with changes in it.
        Assert.NotNull(await _worktrees.RemoveAsync(_repo, worktree, discard: false, ownCommits: 1, Token));
        Assert.True(Directory.Exists(path));

        Assert.Null(await _worktrees.RemoveAsync(_repo, worktree, discard: true, ownCommits: 1, Token));
        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain("worktree-calm-heron", await BranchesAsync());
    }

    [Fact]
    public async Task The_main_checkout_is_never_found_or_removed()
    {
        Assert.SkipWhen(!IsolatedGitLauncher.GitInstalled, "git isn't on PATH.");
        await InitAsync();

        Assert.Null(await _worktrees.FindAsync(_repo, _repo, Token));
        var main = (await _worktrees.ListAsync(_repo, Token))![0];
        Assert.True(main.IsMain);
        Assert.NotNull(await _worktrees.RemoveAsync(_repo, main, discard: true, ownCommits: 0, Token));
        Assert.True(Directory.Exists(Path.Combine(_repo, ".git")));
    }

    private async Task InitAsync()
    {
        await _launcher.RunAsync(_repo, "init", "-q");
        await _launcher.RunAsync(_repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
    }

    /// <summary>Makes a worktree the way Claude Code's <c>--worktree</c> does: its place, its branch, its lock.</summary>
    private async Task<string> AddWorktreeAsync(string name, bool locked)
    {
        await InitAsync();
        var path = GitWorktrees.PathFor(_repo, name);
        await _launcher.RunAsync(_repo, "worktree", "add", "-q", "-b", GitWorktrees.BranchPrefix + name, path);
        if (locked)
        {
            await _launcher.RunAsync(_repo, "worktree", "lock", "--reason", $"claude session {name} (pid 1)", path);
        }
        return path;
    }

    private async Task<string> BranchesAsync()
    {
        var result = await Core.Processes.ProcessRunner.RunAsync(_launcher, new Core.Processes.ProcessStartSpec("git", ["branch", "--list"]) { WorkingDirectory = _repo },
            TimeSpan.FromSeconds(30), TimeProvider.System, Token);
        return result.StandardOutput;
    }
}
