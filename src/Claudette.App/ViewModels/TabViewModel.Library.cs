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
        // A copy: the tab keeps adding to its own totals while the record is saved.
        Tokens = JsonSerializer.Deserialize<TokenTotals>(JsonSerializer.Serialize(State.Tokens, JsonFileStore<TokenTotals>.Options), JsonFileStore<TokenTotals>.Options) ?? new TokenTotals(),
        LastUsed = _services.Time.GetUtcNow(),
        Folder = Folder,
        FirstPrompt = _firstPrompt,
        ClaudeCodeVersion = _session?.ClaudeCodeVersion,
    };

    private void ReleaseLease()
    {
        if (State.SessionId is { } sessionId && TakenOverBy is null)
        {
            _services.Library.Leases.Release(sessionId);
        }
    }
}
