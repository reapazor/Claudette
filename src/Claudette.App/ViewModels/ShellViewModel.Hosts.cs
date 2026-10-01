using Claudette.Core.Git;
using Claudette.Core.Settings;

namespace Claudette.App.ViewModels;

/// <summary>What the shell gives the parts it has handed work to: opening a session from History.</summary>
public sealed partial class ShellViewModel : ISessionOpenerHost
{
    // ---- Opening a session from History (DESIGN.md §9) ---------------------------------------------------------

    void ISessionOpenerHost.SelectTab(TabViewModel tab) => SelectedTab = tab;

    void ISessionOpenerHost.OpenSession(TabState state)
    {
        // A session that worked in a worktree Claude Code made goes back in its main checkout's group.
        state.WorktreeOf ??= GitWorktrees.MainCheckoutOf(state.Folder);
        FolderHistory.Touch(_services.State, state.WorktreeOf ?? state.Folder, _services.Time.GetUtcNow(), _services.Settings.NewTabs.RecentFolderLimit);
        var tab = new TabViewModel(_services, this, state, isRestored: true);
        AddTab(tab);
        SelectedTab = tab;
        SaveTabs();
    }

    void ISessionOpenerHost.Confirm(string title, string message, string confirmText, Func<Task> onConfirm) =>
        Confirm(title, message, confirmText, onConfirm);

    void ISessionOpenerHost.Confirm(string title, string message, string confirmText, Func<Task> onConfirm, string secondaryText, Func<Task> onSecondary) =>
        Confirm(title, message, confirmText, onConfirm, secondaryText, onSecondary);
}
