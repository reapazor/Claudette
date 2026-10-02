namespace Claudette.Core.Git;

/// <summary>
/// Settings → General's rules for removing worktree tabs' worktrees on their own (DESIGN.md §4, "Cleaning up
/// worktrees").
/// </summary>
/// <param name="RemoveMerged">Remove one with nothing of its own: no uncommitted changes, and every commit on another branch.</param>
/// <param name="RemoveInactiveAfterDays">Remove one no tab has used for this many days, keeping a branch with commits of its own; 0 for never.</param>
public sealed record WorktreeCleanupRules(bool RemoveMerged, int RemoveInactiveAfterDays)
{
    /// <summary>A worktree with nothing of its own waits this long after a tab last used it, so one just closed and kept isn't taken at once.</summary>
    public static readonly TimeSpan MergedGrace = TimeSpan.FromDays(1);

    public bool IsOn => RemoveMerged || RemoveInactiveAfterDays > 0;
}

/// <summary>Why a worktree is removed, or that it's kept.</summary>
public enum WorktreeVerdict
{
    Keep,

    /// <summary>Nothing of its own: it and its branch go.</summary>
    Merged,

    /// <summary>Unused for long enough: it goes, and its branch stays when it has commits of its own.</summary>
    Inactive,
}

/// <summary>
/// Which worktrees to remove on their own (DESIGN.md §4, "Cleaning up worktrees"). Only those Claude Code made for
/// worktree tabs (<c>.claude/worktrees/&lt;name&gt;</c>), never one a tab works in, and never one with uncommitted
/// changes, or whose work git couldn't count: removing those would lose work. Files git ignores, such as build output,
/// go with a removed worktree.
/// </summary>
public static class WorktreeCleanup
{
    /// <param name="work">What it holds, or null when git couldn't tell.</param>
    /// <param name="lastUsed">When a tab last worked in it, or when Claudette first saw it.</param>
    public static WorktreeVerdict Decide(GitWorktree worktree, GitWorktreeWork? work, DateTimeOffset lastUsed, WorktreeCleanupRules rules, DateTimeOffset now)
    {
        if (worktree.IsMain || GitWorktrees.MainCheckoutOf(worktree.Path) is null || work is not { HasChanges: false })
        {
            return WorktreeVerdict.Keep;
        }
        var idle = now - lastUsed;
        if (rules.RemoveMerged && work.OwnCommits == 0 && idle >= WorktreeCleanupRules.MergedGrace)
        {
            return WorktreeVerdict.Merged;
        }
        if (rules.RemoveInactiveAfterDays > 0 && idle >= TimeSpan.FromDays(rules.RemoveInactiveAfterDays))
        {
            return WorktreeVerdict.Inactive;
        }
        return WorktreeVerdict.Keep;
    }
}
