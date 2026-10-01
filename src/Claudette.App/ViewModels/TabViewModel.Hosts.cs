using Claudette.App.Conversation;
using Claudette.Core.Sessions;
using Claudette.Platform.Processes;

namespace Claudette.App.ViewModels;

/// <summary>
/// What the tab gives its child view models: the process monitor, changed files, project tools and Remote Control. Each
/// sees only its own host interface; where one area needs another, it goes through the tab.
/// </summary>
public sealed partial class TabViewModel : IProcessMonitorHost, IChangedFilesHost, IProjectToolsHost, IRemoteControlHost
{
    // ---- The process monitor -----------------------------------------------------------------------------------------

    ClaudeSession? IProcessMonitorHost.Session => _session;

    IEnumerable<ToolUseItem> IProcessMonitorHost.ToolItems => Items.OfType<ToolUseItem>();

    string? IProcessMonitorHost.TaskIdFor(string toolUseId) => Tasks.TaskIdFor(toolUseId);

    IReadOnlyList<ProcessSnapshot> IProcessMonitorHost.ProjectJobProcesses(bool includeCommandLines) => ProjectTools.JobProcesses(includeCommandLines);

    ProcessTree? IProcessMonitorHost.ProjectJobTreeHolding(int pid) => ProjectTools.JobTreeHolding(pid);

    void IProcessMonitorHost.ScrollTo(ConversationItem item) => ScrollToRequested?.Invoke(item);

    void IProcessMonitorHost.Confirm(string title, string message, string confirmText, Func<Task> onConfirm) => _shell.Confirm(title, message, confirmText, onConfirm);

    void IProcessMonitorHost.AddNote(string text, NoteKind kind) => _conversation.AddNote(text, kind);

    void IProcessMonitorHost.ProcessesSampled() => _shell.OnTabProcessesSampled();

    // ---- Changed files ---------------------------------------------------------------------------------------------

    void IChangedFilesHost.CopyToLibrary() => CopyToLibrary();

    void IChangedFilesHost.AddNote(string text, NoteKind kind) => _conversation.AddNote(text, kind);

    // ---- Project tools ---------------------------------------------------------------------------------------------

    bool IProjectToolsHost.IsProjectPageShowing => IsSelected && IsSidePanelOpen && IsProjectPage;

    long? IProjectToolsHost.CurrentChangelist => Changelists.Current?.Number;

    void IProjectToolsHost.AddNote(string text, NoteKind kind) => _conversation.AddNote(text, kind);

    void IProjectToolsHost.Confirm(string title, string message, string confirmText, Func<Task> onConfirm) => _shell.Confirm(title, message, confirmText, onConfirm);

    void IProjectToolsHost.InfoRowsChanged() => OnPropertyChanged(nameof(InfoRows));

    void IProjectToolsHost.OpenProjectPage() => OpenProjectPage();

    void IProjectToolsHost.SelectTab() => _shell.SelectTab(Id);

    Task IProjectToolsHost.OpenProjectSettingsAsync(string page, bool startNew) => _shell.OpenProjectSettingsAsync(this, page, startNew);

    // ---- Remote Control --------------------------------------------------------------------------------------------

    ClaudeSession? IRemoteControlHost.Session => _session;

    void IRemoteControlHost.AddNote(string text, NoteKind kind, string? link) => _conversation.AddNote(text, kind, link);

    void IRemoteControlHost.InfoRowsChanged() => OnPropertyChanged(nameof(InfoRows));

    void IRemoteControlHost.CommandTurnEnded() => _checkIns.TurnEnded();

    async Task IRemoteControlHost.RestartSessionAsync()
    {
        await StopSessionAsync();
        await EnsureStartedAsync();
    }
}
