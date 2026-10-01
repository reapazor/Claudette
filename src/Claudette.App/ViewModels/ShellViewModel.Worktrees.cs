using Claudette.Core.Git;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// Worktree tabs (DESIGN.md §4, "Worktree tabs"): a tab whose Claude Code works in a git worktree of its own, so its
/// edits don't meet those of the other tabs in the folder, and removing the worktree once its last tab closes.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// <b>New tab in a worktree</b>: a tab in the group's folder whose Claude Code starts with <c>--worktree</c> and a
    /// name Claudette picks, so a restart before its first turn opens the same worktree rather than another.
    /// </summary>
    [RelayCommand]
    private Task NewWorktreeTabAsync(TabGroupViewModel? group) =>
        group is null ? Task.CompletedTask : OpenWorktreeTabAsync(group.Folder);

    /// <summary>Opens a new tab that works in a new worktree of <paramref name="folder"/>'s repository, and selects it.</summary>
    public async Task OpenWorktreeTabAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        var main = FolderHistory.Normalize(folder);
        var root = await _services.Git.GetRepositoryRootAsync(main);
        if (root is null)
        {
            Confirm($"{Path.GetFileName(main)} isn't in a git repository", "A worktree is a second checkout of a git repository, so a folder outside one can't have one.",
                "OK", () => Task.CompletedTask);
            return;
        }
        var name = GitWorktrees.NewName(_services.Random, candidate =>
            Directory.Exists(GitWorktrees.PathFor(root, candidate)) || AllTabs.Any(t => t.State.NewWorktree == candidate));
        FolderHistory.Touch(_services.State, main, _services.Time.GetUtcNow(), _services.Settings.NewTabs.RecentFolderLimit);
        var state = new TabState
        {
            Folder = main,
            WorktreeOf = main,
            NewWorktree = name,
            SyncToLibrary = _services.Settings.Sessions.SyncNewTabs,
            RemoteControl = _services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp,
        };
        var tab = new TabViewModel(_services, this, state, isRestored: false);
        AddTab(tab);
        SelectedTab = tab;
        SaveTabs();
        await tab.EnsureStartedAsync();
    }

    /// <summary>The selected tab's group folder can have worktrees: the palette offers one.</summary>
    private bool CanOpenWorktreeTab(TabViewModel tab) => Groups.FirstOrDefault(g => g.Tabs.Contains(tab)) is { CanMakeWorktrees: true };

    /// <summary>
    /// Tabs closed: for each worktree no open tab still works in, asks whether to remove it. One with nothing of its
    /// own is offered for removal; one holding changes or commits is kept unless the user says to discard them. When
    /// several closed at once, only those with nothing of their own are offered, and the rest are kept.
    /// </summary>
    internal async Task OfferToRemoveWorktreesAsync(IReadOnlyList<TabState> closed)
    {
        var found = new List<(string Main, GitWorktree Worktree, GitWorktreeWork? Work)>();
        foreach (var state in closed)
        {
            if (state.WorktreeOf is not { } main || !Directory.Exists(main))
            {
                continue;
            }
            var path = state.NewWorktree is { } name
                ? await _services.Git.GetRepositoryRootAsync(main) is { } root ? GitWorktrees.PathFor(root, name) : null
                : state.Folder;
            if (path is null || !Directory.Exists(path) || found.Any(f => FolderHistory.SamePath(f.Worktree.Path, path))
                || AllTabs.Any(t => FolderHistory.SamePath(t.Folder, path)))
            {
                continue;
            }
            var worktrees = new GitWorktrees(_services.Git);
            if (await worktrees.FindAsync(main, path) is { } worktree)
            {
                found.Add((main, worktree, await worktrees.InspectAsync(worktree)));
            }
        }
        if (found.Count == 1)
        {
            OfferToRemove(found[0].Main, found[0].Worktree, found[0].Work);
        }
        else if (found.Count > 1)
        {
            OfferToRemoveEmpty(found);
        }
    }

    private void OfferToRemove(string main, GitWorktree worktree, GitWorktreeWork? work)
    {
        var name = Path.GetFileName(worktree.Path);
        var branch = worktree.Branch is { } b ? $" Its branch {b} goes too." : "";
        if (work is { IsEmpty: true })
        {
            Confirmation = new ConfirmationViewModel($"Remove the worktree {name}?",
                $"The tab you closed worked in it. It has no changes and no commits of its own, so nothing is lost.{branch}",
                "Remove", () => RemoveWorktreeAsync(main, worktree, discard: false, 0), () => Confirmation = null, cancelText: "Keep");
            return;
        }
        Confirmation = new ConfirmationViewModel($"Keep the worktree {name}?",
            $"The tab you closed worked in it, and it has {WorkText(work)}. Keep it to carry on there from History, or remove it with that work.",
            "Keep", () => Task.CompletedTask, () => Confirmation = null,
            "Remove and discard", () => RemoveWorktreeAsync(main, worktree, discard: true, work?.OwnCommits ?? 0), cancelText: null);
    }

    private void OfferToRemoveEmpty(List<(string Main, GitWorktree Worktree, GitWorktreeWork? Work)> found)
    {
        var empty = found.Where(f => f.Work is { IsEmpty: true }).ToList();
        if (empty.Count == 0)
        {
            return;
        }
        var kept = found.Count - empty.Count;
        var names = string.Join(", ", empty.Select(f => Path.GetFileName(f.Worktree.Path)));
        Confirmation = new ConfirmationViewModel($"Remove the worktrees those tabs worked in?",
            $"{names} {(empty.Count == 1 ? "has" : "have")} no changes and no commits of {(empty.Count == 1 ? "its" : "their")} own, so nothing is lost."
            + (kept == 0 ? "" : $" {(kept == 1 ? "One more holds" : $"{kept} more hold")} work, and {(kept == 1 ? "stays" : "stay")}."),
            empty.Count == 1 ? "Remove it" : $"Remove {empty.Count}",
            async () =>
            {
                foreach (var (main, worktree, _) in empty)
                {
                    await RemoveWorktreeAsync(main, worktree, discard: false, 0);
                }
            },
            () => Confirmation = null, cancelText: "Keep");
    }

    private static string WorkText(GitWorktreeWork? work) => work switch
    {
        null => "work git couldn't count",
        { HasChanges: true, OwnCommits: > 0 } => $"uncommitted changes and {Commits(work.OwnCommits)} on no other branch",
        { HasChanges: true } => "uncommitted changes",
        _ => $"{Commits(work.OwnCommits)} on no other branch",
    };

    private static string Commits(int count) => count == 1 ? "a commit" : $"{count} commits";

    private async Task RemoveWorktreeAsync(string main, GitWorktree worktree, bool discard, int ownCommits)
    {
        if (await new GitWorktrees(_services.Git).RemoveAsync(main, worktree, discard, ownCommits) is { } error)
        {
            Confirm($"Couldn't remove the worktree {Path.GetFileName(worktree.Path)}", error, "OK", () => Task.CompletedTask);
        }
    }
}
