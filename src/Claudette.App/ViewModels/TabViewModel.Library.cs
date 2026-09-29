using System.Text.Json;
using Claudette.App.Conversation;
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

    /// <summary>A copy (<b>Open a copy</b>, or a conflict copy) that hasn't had its first turn yet still has the original's id.</summary>
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
        _services.SaveState();
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
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
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
            if (library.Library.GetTranscriptPath(sessionId) is null)
            {
                return true;
            }
            switch (library.CheckLease(sessionId))
            {
                case LeaseStatus.HeldByOther other:
                    await OnTakenOverAsync(other.Machine);
                    return false;
                case LeaseStatus.Mine:
                    return true;
                default:
                    library.Leases.Acquire(sessionId, library.Library.GetSessionFolder(sessionId));
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
            _services.Library.Leases.Release(sessionId);
        }
    }
}
