using Claudette.App.Conversation;
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

    /// <summary>
    /// The extra folders changed while Claude worked: Claude Code starts again with them once it has nothing left to
    /// do, the messages sent meanwhile included. Any start takes them, so a start clears it.
    /// </summary>
    private bool _restartForExtraFolders;

    /// <summary>How many turns this tab's sessions have started, so a restart can tell whether another began.</summary>
    private int _turnsStarted;

    /// <summary>
    /// The worktree Claude Code was asked to make was already there as it started: a restart before the first turn opens
    /// it again, and its setup actions have run already (DESIGN.md §18, "Setting up a new worktree").
    /// </summary>
    private bool _worktreeExistedAtStart;

    /// <summary>Notes, before Claude Code starts with <c>--worktree</c>, whether that worktree is there already.</summary>
    private async Task NoteWhetherWorktreeExistsAsync()
    {
        _worktreeExistedAtStart = State.NewWorktree is { } name && await _services.Git.GetRepositoryRootAsync(Folder) is { } root
            && Directory.Exists(Core.Git.GitWorktrees.PathFor(root, name));
    }

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
            OnScratchPadFolderChanged();
            _conversation.AddNote($"Working in the worktree {FolderName}, on its own branch: {State.Folder}.");
            if (!_worktreeExistedAtStart && State.WorktreeOf is { } main)
            {
                // Made just now, not opened again after a restart: the folder's actions for a new worktree run (DESIGN.md §18).
                var trusted = !State.WithoutProjectSettings && _services.IsFolderTrusted(main);
                _ = ProjectTools.RunWorktreeSetupAsync(main, State.Folder, trusted);
            }
        }
        OnPropertyChanged(nameof(WorktreeName));
        OnPropertyChanged(nameof(WorktreeTip));
        OnPropertyChanged(nameof(InfoRows));
        OnBranchFolderChanged();
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

    /// <param name="afterTurn">The turn whose end asked for it: a turn started since then is running.</param>
    private async Task RestartForExtraFoldersAsync(int? afterTurn = null)
    {
        // A message sent while Claude worked runs next: a restart now would take it with the session, so the restart
        // waits for that turn to end too.
        if (afterTurn is { } turn && turn != _turnsStarted || Items.OfType<UserMessageItem>().Any(m => m.IsQueued))
        {
            _restartForExtraFolders = true;
            return;
        }
        _restartForExtraFolders = false;
        await StopSessionAsync();
        _conversation.AddNote(State.ExtraFolders.Count == 0
            ? "Started Claude Code again without extra folders."
            : $"Started Claude Code again with {(State.ExtraFolders.Count == 1 ? "an extra folder" : $"{State.ExtraFolders.Count} extra folders")}: {string.Join(", ", State.ExtraFolders)}.");
        Status = TabStatus.NotStarted;
        await EnsureStartedAsync();
    }
}
