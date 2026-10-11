using Claudette.App.Conversation;
using Claudette.Core.Sessions;
using Claudette.Platform.Processes;

namespace Claudette.App.ViewModels;

/// <summary>
/// What the tab gives its child view models: the process monitor, changed files, project tools, Remote Control,
/// Perforce and the context indicator. Each sees only its own host interface, over the <see cref="ITabAreaHost"/> they share; where one area needs
/// another, it goes through the tab.
/// </summary>
public sealed partial class TabViewModel : IProcessMonitorHost, IChangedFilesHost, IProjectToolsHost, IRemoteControlHost, IPerforceHost, IContextHost
{
    // ---- Every area ------------------------------------------------------------------------------------------------

    ClaudeSession? ITabAreaHost.Session => _session;

    void ITabAreaHost.AddNote(string text, NoteKind kind, string? link) => _conversation.AddNote(text, kind, link);

    void ITabAreaHost.Confirm(string title, string message, string confirmText, Func<Task> onConfirm) => _shell.Confirm(title, message, confirmText, onConfirm);

    void ITabAreaHost.InfoRowsChanged() => OnPropertyChanged(nameof(InfoRows));

    // ---- The process monitor -----------------------------------------------------------------------------------------

    IEnumerable<ToolUseItem> IProcessMonitorHost.ToolItems => Items.OfType<ToolUseItem>();

    string? IProcessMonitorHost.TaskIdFor(string toolUseId) => Tasks.TaskIdFor(toolUseId);

    IReadOnlyList<ProcessSnapshot> IProcessMonitorHost.ProjectJobProcesses(bool includeCommandLines) => ProjectTools.Runs.JobProcesses(includeCommandLines);

    ProcessTree? IProcessMonitorHost.ProjectJobTreeHolding(int pid) => ProjectTools.Runs.JobTreeHolding(pid);

    void IProcessMonitorHost.ScrollTo(ConversationItem item) => ScrollTo(item);

    void IProcessMonitorHost.ProcessesSampled() => _shell.OnTabProcessesSampled();

    // ---- Changed files ---------------------------------------------------------------------------------------------

    void IChangedFilesHost.CopyToLibrary() => CopyToLibrary();

    bool IChangedFilesHost.IsSelected => IsSelected;

    void IChangedFilesHost.AllReviewedChanged()
    {
        MarkShownChanged();
        // Claudette on the composer stamps it (DESIGN.md §5). Not while the tab is still being made: a restored tab's saved
        // state sets it before ChangedFiles is there.
        if (IsSelected && ChangedFiles is { AllReviewed: true })
        {
            TellMascot(mascot => mascot.AllReviewed());
        }
    }

    // ---- Project tools ---------------------------------------------------------------------------------------------

    bool IProjectToolsHost.IsProjectPageShowing => IsSelected && IsSidePanelOpen && IsProjectPage;

    long? IProjectToolsHost.CurrentChangelist => Perforce.CurrentChangelist;

    void IProjectToolsHost.OpenProjectPage() => OpenProjectPage();

    void IProjectToolsHost.SelectTab() => _shell.SelectTab(Id);

    Task IProjectToolsHost.OpenProjectSettingsAsync(string page, bool startNew) => _shell.OpenProjectSettingsAsync(this, page, startNew);

    // ---- Remote Control --------------------------------------------------------------------------------------------

    void IRemoteControlHost.CommandTurnEnded() => _checkIns.TurnEnded();

    async Task IRemoteControlHost.RestartSessionAsync()
    {
        await StopSessionAsync();
        await EnsureStartedAsync();
    }

    // ---- Perforce --------------------------------------------------------------------------------------------------

    Task IPerforceHost.SendRawAsync(string text) => SendRawAsync(text);

    void IPerforceHost.WaitOnUser(object key)
    {
        _waitingOnUser.Add(key);
        _checkIns.SetWaitingOnUser(true);
        UpdateStatus();
    }

    void IPerforceHost.Resolved(object key) => PermissionResolved(key);

    void IPerforceHost.LinkValuesChanged() => ProjectTools.OnLinkValuesChanged();

    // ---- Context and tokens ----------------------------------------------------------------------------------------

    string? IContextHost.ModelDisplayName(string? id) => ModelDisplayName(id);

    void IContextHost.TurnTokensChanged() => Working.Refresh();
}
