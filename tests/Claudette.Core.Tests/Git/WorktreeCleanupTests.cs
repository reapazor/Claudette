using Claudette.Core.Git;

namespace Claudette.Core.Tests.Git;

/// <summary>Which worktrees are removed on their own (DESIGN.md §4, "Cleaning up worktrees").</summary>
public sealed class WorktreeCleanupTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

    private static readonly string Checkout = Path.Combine(Path.GetTempPath(), "repo");

    private static GitWorktree Worktree(string name = "brisk-otter") =>
        new(GitWorktrees.PathFor(Checkout, name), GitWorktrees.BranchPrefix + name, IsLocked: true, IsMain: false);

    private static readonly GitWorktreeWork Empty = new(HasChanges: false, OwnCommits: 0);

    private static readonly WorktreeCleanupRules Merged = new(RemoveMerged: true, RemoveInactiveAfterDays: 0);

    private static readonly WorktreeCleanupRules Inactive = new(RemoveMerged: false, RemoveInactiveAfterDays: 14);

    [Fact]
    public void Nothing_is_removed_while_the_rules_are_off()
    {
        var rules = new WorktreeCleanupRules(false, 0);

        Assert.False(rules.IsOn);
        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(Worktree(), Empty, Now.AddYears(-1), rules, Now));
    }

    [Fact]
    public void A_worktree_with_nothing_of_its_own_goes_a_day_after_it_was_last_used()
    {
        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(Worktree(), Empty, Now.AddHours(-23), Merged, Now));
        Assert.Equal(WorktreeVerdict.Merged, WorktreeCleanup.Decide(Worktree(), Empty, Now.AddDays(-1), Merged, Now));
        // Commits on no other branch aren't merged.
        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(Worktree(), Empty with { OwnCommits = 2 }, Now.AddDays(-30), Merged, Now));
    }

    [Fact]
    public void An_unused_worktree_goes_after_the_days_chosen_even_with_commits_of_its_own()
    {
        var commits = Empty with { OwnCommits = 3 };

        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(Worktree(), commits, Now.AddDays(-13), Inactive, Now));
        Assert.Equal(WorktreeVerdict.Inactive, WorktreeCleanup.Decide(Worktree(), commits, Now.AddDays(-14), Inactive, Now));
        // With both rules on, an empty one counts as merged, so its branch goes too.
        Assert.Equal(WorktreeVerdict.Merged, WorktreeCleanup.Decide(Worktree(), Empty, Now.AddDays(-20), new WorktreeCleanupRules(true, 14), Now));
    }

    [Fact]
    public void Uncommitted_work_or_work_git_couldnt_count_keeps_it()
    {
        var rules = new WorktreeCleanupRules(true, 7);

        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(Worktree(), Empty with { HasChanges = true }, Now.AddYears(-1), rules, Now));
        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(Worktree(), null, Now.AddYears(-1), rules, Now));
    }

    [Fact]
    public void Only_worktrees_Claude_Code_made_for_worktree_tabs_are_considered()
    {
        var rules = new WorktreeCleanupRules(true, 7);
        var elsewhere = new GitWorktree(Path.Combine(Path.GetTempPath(), "my-own-worktree"), "feature", IsLocked: false, IsMain: false);
        var main = new GitWorktree(Checkout, "main", IsLocked: false, IsMain: true);

        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(elsewhere, Empty, Now.AddYears(-1), rules, Now));
        Assert.Equal(WorktreeVerdict.Keep, WorktreeCleanup.Decide(main, Empty, Now.AddYears(-1), rules, Now));
    }
}
