using Claudette.App.Conversation;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.App.ViewModels;

/// <summary>
/// What every area of a tab gets from it (the process monitor, changed files, Remote Control, Perforce, the context
/// indicator and so on): who the tab is, its saved state and session, and ways to tell the user something. Each area's
/// own host interface adds what only it needs; the tab implements them in <c>TabViewModel.Hosts.cs</c>.
/// </summary>
internal interface ITabAreaHost
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>The tab's working folder.</summary>
    string Folder { get; }

    /// <summary>What's saved for the tab.</summary>
    TabState State { get; }

    /// <summary>The tab's session while it runs.</summary>
    ClaudeSession? Session { get; }

    /// <summary>A note in the conversation, optionally with a link.</summary>
    void AddNote(string text, NoteKind kind = NoteKind.Info, string? link = null);

    /// <summary>A plain confirmation over the whole window.</summary>
    void Confirm(string title, string message, string confirmText, Func<Task> onConfirm);

    /// <summary>The tab info card shows something new.</summary>
    void InfoRowsChanged();
}
