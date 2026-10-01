using System.Text.Json;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Diffs;
using Claudette.Core.Library;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>The tab's part in the session library (DESIGN.md §9).</summary>
public sealed partial class TabViewModel
{
    // ---- Syncing (DESIGN.md §9, "Session library") ------------------------------------------------------------

    /// <summary>
    /// The tab copies its session to the library after each turn, so another machine can open it from History. Off
    /// unless the tab opted in; a tab that doesn't sync never writes to the library and ignores leases.
    /// </summary>
    public bool SyncToLibrary => State.SyncToLibrary;

    /// <summary>
    /// This tab's Claude Code was started as a copy (<b>Open a copy</b>, a branch, or a conflict copy) and hasn't had its
    /// first turn yet, so it still has the original's id. <see cref="TabState.ForkOnNextStart"/> stays set until then too.
    /// </summary>
    private bool _forkAwaitingId;

    /// <summary><b>Sync to other machines</b> in the tab's menu.</summary>
    [RelayCommand]
    private void ToggleSyncToLibrary() => SetSyncToLibrary(!State.SyncToLibrary);

    /// <summary>
    /// Turns syncing on or off. On copies the session to the library straight away, in the background, and takes its
    /// lease, unless a turn is running: then it's copied when the turn ends. Off stops copying and releases the lease,
    /// leaving the copy already in the library as it was, so other machines still see it.
    /// </summary>
    public void SetSyncToLibrary(bool on)
    {
        if (on == State.SyncToLibrary)
        {
            return;
        }
        if (!on)
        {
            State.SyncToLibrary = false;
            SyncChanged();
            ReleaseLease();
            return;
        }
        // A copy still has the original's id until its first turn: it syncs under its own id from then on.
        var copyPending = State.ForkOnNextStart || _forkAwaitingId;
        if (!copyPending && !IsReadOnly && State.SessionId is { } sessionId && _services.Library.Library.GetTranscriptPath(sessionId) is not null
            && _services.Library.CheckLease(sessionId) is LeaseStatus.HeldByOther other)
        {
            // Another machine carries this session on: copying it from here would overwrite what it wrote.
            _conversation.AddNote($"{other.Machine} has this session open, so this tab can't sync it too. Sync stays off.", NoteKind.Warning);
            // The menu item ticked itself as it was clicked: reading the value again unticks it.
            OnPropertyChanged(nameof(SyncToLibrary));
            return;
        }
        State.SyncToLibrary = true;
        SyncChanged();
        if (!copyPending && !IsWorking)
        {
            CopyToLibrary();
        }
    }

    private void SyncChanged()
    {
        OnPropertyChanged(nameof(SyncToLibrary));
        OnSyncNowChanged();
        _services.SaveState();
    }

    /// <summary>
    /// <b>Sync now</b> can run: the tab syncs, has a session of its own, and Claude Code isn't writing its transcript. A
    /// copy that hasn't had its first turn still has the original's id, so it waits for its own.
    /// </summary>
    public bool CanSyncNow => State.SyncToLibrary && State.SessionId is not null && !IsWorking && !IsReadOnly && !State.ForkOnNextStart && !_forkAwaitingId;

    /// <summary><b>Sync now</b>'s tip: what it does, or why it's disabled.</summary>
    public string SyncNowTip =>
        IsReadOnly ? $"This session continued on {TakenOverBy}"
        : IsWorking ? "Claude is working: the session is copied when the turn ends"
        : State.SessionId is null || State.ForkOnNextStart || _forkAwaitingId ? "There's nothing to copy until the first message"
        : "Copy this session to the session library now, without waiting for the next turn";

    /// <summary>
    /// <b>Sync now</b> in the tab's menu: copies the session to the library straight away, every file again, rather than
    /// waiting for the next turn (DESIGN.md §9, "Writing"). It catches the library up after a copy that failed while its
    /// folder was unavailable, or a rename since the last turn, and says in the conversation how it went.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private async Task SyncNowAsync()
    {
        var sessionId = State.SessionId!;
        var library = _services.Library;
        // Leases are only refreshed once a minute: another machine may have taken the session over since, and copying
        // from here would overwrite what it wrote.
        if (library.Library.GetTranscriptPath(sessionId) is not null && library.CheckLease(sessionId) is LeaseStatus.HeldByOther other)
        {
            await OnTakenOverAsync(other.Machine);
            return;
        }
        var result = await library.CopyToLibraryAsync(LibraryRecord(), State.TranscriptPath, () => State.SyncToLibrary && !IsReadOnly, force: true);
        switch (result)
        {
            case LibraryCopyResult.Copied:
                _conversation.AddNote("Copied this session to the session library.");
                break;
            case LibraryCopyResult.NoTranscript:
                _conversation.AddNote("Couldn't copy this session to the session library: its transcript isn't on this machine.", NoteKind.Warning);
                break;
            case LibraryCopyResult.Failed failed:
                _conversation.AddNote($"Couldn't copy this session to the session library: {failed.Reason}", NoteKind.Warning);
                break;
            case LibraryCopyResult.NotHeld when !IsReadOnly:
                _conversation.AddNote("Didn't copy this session to the session library: its lease there couldn't be read, or another machine has it open. Try again in a moment.", NoteKind.Warning);
                break;
        }
    }

    private void OnSyncNowChanged()
    {
        OnPropertyChanged(nameof(CanSyncNow));
        OnPropertyChanged(nameof(SyncNowTip));
        SyncNowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Copies the session to the library in the background, and takes its lease with the copy, if the tab syncs. A tab
    /// that doesn't sync never writes to the library.
    /// </summary>
    private void CopyToLibrary()
    {
        if (State.SessionId is not null && State.SyncToLibrary && !IsReadOnly)
        {
            _ = _services.Library.CopyToLibraryAsync(LibraryRecord(), State.TranscriptPath, () => State.SyncToLibrary);
        }
    }

    // ---- One machine at a time (DESIGN.md §9) ----------------------------------------------------------------------

    /// <summary>
    /// Another machine took this session over. The tab stops its process and becomes read-only, so the two machines
    /// never write the same session (DESIGN.md §9, "One machine at a time").
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(CanSyncNow), nameof(SyncNowTip))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SyncNowCommand))]
    public partial string? TakenOverBy { get; set; }

    public bool IsReadOnly => TakenOverBy is not null;

    public string ReadOnlyText => $"This session continued on {TakenOverBy}. The tab is read-only here; open it again from History to continue on this machine.";

    public async Task OnTakenOverAsync(string machine)
    {
        TakenOverBy = machine;
        OnPropertyChanged(nameof(ReadOnlyText));
        _conversation.AddNote($"This session was taken over by {machine}.", NoteKind.Warning);
        await StopSessionAsync();
        Status = TabStatus.Exited;
    }

    /// <summary>What the library keeps about this session, as of now. Built on the UI thread, which owns the state.</summary>
    private SessionRecord LibraryRecord() => new()
    {
        SessionId = State.SessionId!,
        Name = DisplayName,
        AutoName = State.AutoName,
        UserName = State.UserName,
        Model = _modelId,
        Effort = Effort,
        PermissionMode = PermissionMode,
        Overrides = Copy(State.Overrides) ?? new TabOverrides(),
        // A copy: the tab keeps adding to its own totals while the record is saved.
        Tokens = Copy(State.Tokens) ?? new TokenTotals(),
        LastUsed = _services.Time.GetUtcNow(),
        Folder = Folder,
        FirstPrompt = _firstPrompt,
        ClaudeCodeVersion = _session?.ClaudeCodeVersion,
        ReviewedFiles = ReviewedFiles.ToRecord(State.ReviewedFiles, Folder),
    };

    /// <summary>
    /// Takes the session's library lease as a tab that syncs starts it, so another machine sees it's in use from the
    /// start (DESIGN.md §9, "One machine at a time"). A session that isn't in the library yet gets its lease with its
    /// first copy. Returns false when another machine holds a live lease: the session carried on there, so the tab
    /// becomes read-only instead of starting.
    /// </summary>
    private async Task<bool> ClaimLeaseAsync()
    {
        if (State.SessionId is not { } sessionId)
        {
            return true;
        }
        var library = _services.Library;
        try
        {
            // File reads on the library folder, which may be a slow or offline drive: not on the UI thread.
            var status = await Task.Run(() => library.Library.GetTranscriptPath(sessionId) is null ? null : library.CheckLease(sessionId));
            if (status is null)
            {
                return true;
            }
            switch (status)
            {
                case LeaseStatus.HeldByOther other:
                    await OnTakenOverAsync(other.Machine);
                    return false;
                case LeaseStatus.Mine:
                case LeaseStatus.Unreadable:
                    // Unreadable: perhaps a lease mid-sync. The session still works here; the copy after the next turn
                    // takes the lease if it's free by then, and refuses if another machine has it.
                    return true;
                default:
                    await Task.Run(() => library.Leases.TakeOver(sessionId, library.Library.GetSessionFolder(sessionId)));
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The library folder may be unavailable (a sync client offline): the session still works on this machine.
            return true;
        }
    }

    private static T? Copy<T>(T value) where T : class, new() => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonFileStore<T>.Options), JsonFileStore<T>.Options);

    private void ReleaseLease()
    {
        if (State.SessionId is { } sessionId && TakenOverBy is null)
        {
            // Deleting a file in the library folder: not on the UI thread.
            var leases = _services.Library.Leases;
            _ = Task.Run(() => leases.Release(sessionId));
        }
    }
}
