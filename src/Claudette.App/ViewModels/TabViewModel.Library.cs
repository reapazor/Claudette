using System.Text.Json;
using Claudette.App.Conversation;
using Claudette.Core.Library;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>The tab's part in the session library (DESIGN.md §9).</summary>
public sealed partial class TabViewModel
{
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
    /// Takes the session's library lease as the tab starts it, so another machine sees it's in use from the start
    /// (DESIGN.md §9, "One machine at a time"). A session that isn't in the library yet gets its lease with its first
    /// copy. Returns false when another machine holds a live lease: the session carried on there, so the tab becomes
    /// read-only instead of starting.
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
