using Claudette.Core;
using Claudette.Core.Git;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The git branch on the tab's row (DESIGN.md §4, "Sidebar"): what the tab's folder has checked out, read from its
/// <c>.git</c> files, at the right of the row's second line. A tab in a worktree shows the worktree's name, and a
/// detached HEAD its short commit. It's read again when the tab is selected, after each turn, when Claudette comes to
/// the front and when the tab's folder changes, since Claude or the user may have switched branches.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>What's checked out, as last read; null outside a git repository.</summary>
    private (string? Branch, string? Commit)? _head;

    /// <summary>The badge's text: the branch, the worktree's name, or a short commit; null outside a git repository.</summary>
    public string? BranchBadge =>
        _head is not { } head ? null
        : IsInWorktree ? WorktreeName
        : head.Branch ?? (head.Commit is { } commit ? ProjectIdentity.ShortCommit(commit) : null);

    /// <summary>Settings → Appearance → Show git branch on tab rows, and something to show.</summary>
    public bool ShowBranchBadge => _services.Settings.Appearance.ShowBranchOnTabs && BranchBadge is not null;

    /// <summary>The worktree's branch icon is in the badge; without the badge, it's on the name line.</summary>
    public bool ShowWorktreeIcon => IsInWorktree && !ShowBranchBadge;

    /// <summary>The badge's tip: <i>"On branch feature/auth"</i>, the worktree and its branch, or the detached commit.</summary>
    public string? BranchBadgeTip
    {
        get
        {
            if (_head is not { } head)
            {
                return null;
            }
            var on = head.Branch is { } branch ? $"on branch {branch}"
                : head.Commit is { } commit ? $"at {ProjectIdentity.ShortCommit(commit)}, not on a branch"
                : "on no commit yet";
            var tip = !IsInWorktree ? Formats.Capitalize(on)
                : State.NewWorktree is not null ? WorktreeTip
                : $"Works in the worktree {WorktreeName}, {on}";
            return $"{tip}. Click to copy the branch's name.";
        }
    }

    /// <summary>The branch to copy: a worktree's own before Claude Code has made it, else what's checked out.</summary>
    private string? BranchName => State.NewWorktree is { } pending ? GitWorktrees.BranchPrefix + pending : _head?.Branch ?? _head?.Commit;

    /// <summary>Reads what the tab's folder has checked out again, and the badge follows.</summary>
    internal void RefreshBranch()
    {
        var head = GitInfo.TryGetHead(Folder);
        if (head == _head)
        {
            return;
        }
        _head = head;
        OnBranchChanged();
    }

    /// <summary>The tab works in another folder, or a worktree it's in was made: read it, and the badge follows.</summary>
    private void OnBranchFolderChanged()
    {
        _head = GitInfo.TryGetHead(Folder);
        OnBranchChanged();
    }

    private void OnBranchChanged()
    {
        OnPropertyChanged(nameof(BranchBadge));
        OnPropertyChanged(nameof(ShowBranchBadge));
        OnPropertyChanged(nameof(ShowWorktreeIcon));
        OnPropertyChanged(nameof(BranchBadgeTip));
    }

    /// <summary>The badge's <b>Copy branch name</b>: the branch, or the commit on a detached HEAD.</summary>
    [RelayCommand]
    private Task CopyBranchAsync() => BranchName is { } name ? _services.Platform.SetClipboardTextAsync(name) : Task.CompletedTask;
}
