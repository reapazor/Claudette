using Claudette.Core.Settings;

namespace Claudette.App.ViewModels;

public sealed partial class TabViewModel
{
    // ---- Worktree tabs and extra folders (DESIGN.md §4) ------------------------------------------------------

    /// <summary>The folder whose group the tab is in: the main checkout for a tab in a worktree, else its own folder.</summary>
    public string GroupFolder => State.WorktreeOf ?? State.Folder;

    /// <summary>The tab works in a git worktree of its own, or will once Claude Code has made it.</summary>
    public bool IsInWorktree => State.WorktreeOf is not null;

    /// <summary>The worktree's name, such as <c>brisk-otter</c>, or null when the tab isn't in one.</summary>
    public string? WorktreeName => State.NewWorktree ?? (IsInWorktree ? FolderName : null);

    /// <summary>The tip on the tab row's branch icon.</summary>
    public string? WorktreeTip => WorktreeName is not { } name ? null
        : State.NewWorktree is not null ? $"Works in the worktree {name}, which Claude Code makes when it starts"
        : $"Works in the worktree {name}, on its own branch";

    /// <summary>The folders Claude may also read and edit (<c>--add-dir</c>).</summary>
    public IReadOnlyList<string> ExtraFolders => State.ExtraFolders;

    /// <summary>The extra folders changed while Claude worked: Claude Code starts again with them once the turn ends.</summary>
    private bool _restartForExtraFolders;

    private void AddWorktreeRows(List<InfoRow> rows)
    {
        if (WorktreeName is { } name && State.WorktreeOf is { } main)
        {
            rows.Add(new InfoRow("Worktree", State.NewWorktree is null ? $"{name}, of {main}" : $"{name} (to be made), of {main}"));
        }
        if (State.ExtraFolders.Count > 0)
        {
            rows.Add(new InfoRow("Extra folders", string.Join("\n", State.ExtraFolders)));
        }
    }

    /// <summary>
    /// <c>system/init</c> says where Claude Code works. Started with <c>--worktree</c>, that's the worktree it made: the
    /// tab works there from now on, so changed files, the git view, <c>@</c> files and project tools follow, and later
    /// starts resume in it. It stays in the main checkout's group.
    /// </summary>
    private void OnWorkingFolderReported(string? cwd)
    {
        if (State.NewWorktree is null)
        {
            return;
        }
        State.NewWorktree = null;
        if (cwd is { Length: > 0 } && Directory.Exists(cwd) && !FolderHistory.SamePath(cwd, State.Folder))
        {
            State.WorktreeOf ??= State.Folder;
            State.Folder = FolderHistory.Normalize(cwd);
            OnPropertyChanged(nameof(Folder));
            OnPropertyChanged(nameof(FolderName));
            OnPropertyChanged(nameof(DisplayName));
            ChangedFiles.OnFolderChanged();
            ProjectTools.ReloadCustomActions();
            _conversation.AddNote($"Working in the worktree {FolderName}, on its own branch: {State.Folder}.");
        }
        OnPropertyChanged(nameof(WorktreeName));
        OnPropertyChanged(nameof(WorktreeTip));
        OnPropertyChanged(nameof(InfoRows));
        _services.SaveState();
    }

    /// <summary>
    /// Tab settings changed the extra folders. Claude Code takes them when it starts, so a running one starts again,
    /// keeping the conversation; one in the middle of a turn, once the turn ends.
    /// </summary>
    public async Task SetExtraFoldersAsync(IReadOnlyList<string> folders)
    {
        if (folders.SequenceEqual(State.ExtraFolders, StringComparer.Ordinal))
        {
            return;
        }
        State.ExtraFolders = [.. folders];
        _services.SaveState();
        OnPropertyChanged(nameof(ExtraFolders));
        OnPropertyChanged(nameof(InfoRows));
        if (_session is null)
        {
            return;
        }
        if (IsWorking)
        {
            _restartForExtraFolders = true;
            _conversation.AddNote("The extra folders apply once this turn ends, when Claude Code starts again.");
            return;
        }
        await RestartForExtraFoldersAsync();
    }

    private async Task RestartForExtraFoldersAsync()
    {
        _restartForExtraFolders = false;
        await StopSessionAsync();
        _conversation.AddNote(State.ExtraFolders.Count == 0
            ? "Started Claude Code again without extra folders."
            : $"Started Claude Code again with {(State.ExtraFolders.Count == 1 ? "an extra folder" : $"{State.ExtraFolders.Count} extra folders")}: {string.Join(", ", State.ExtraFolders)}.");
        Status = TabStatus.NotStarted;
        await EnsureStartedAsync();
    }
}
