using System.Text.Json;
using Claudette.App.Services;
using Claudette.Core.Diffs;
using Claudette.Core.Git;
using Claudette.Core.Library;
using Claudette.Core.Settings;

namespace Claudette.App.ViewModels;

/// <summary>What opening a session from History needs from the shell.</summary>
internal interface ISessionOpenerHost
{
    IEnumerable<TabViewModel> AllTabs { get; }

    /// <summary>Selects a tab already open on the session.</summary>
    void SelectTab(TabViewModel tab);

    /// <summary>Opens a tab on a session that resumes once the tab is selected, and selects it.</summary>
    void OpenSession(TabState state);

    /// <summary>A plain confirmation over the whole window.</summary>
    void Confirm(string title, string message, string confirmText, Func<Task> onConfirm);

    /// <summary>A confirmation with a second choice besides the main one.</summary>
    void Confirm(string title, string message, string confirmText, Func<Task> onConfirm, string secondaryText, Func<Task> onSecondary);
}

/// <summary>
/// Opens a past session from History (DESIGN.md §9): this machine's copy where Claude Code keeps it, or the session
/// library's, possibly recorded on another machine. One machine has a session at a time, so a session open elsewhere is
/// taken over or opened as a copy, and a conflict copy always opens as a copy of its own.
/// </summary>
internal sealed class SessionOpener
{
    private readonly AppServices _services;
    private readonly ISessionOpenerHost _host;

    public SessionOpener(AppServices services, ISessionOpenerHost host)
    {
        _services = services;
        _host = host;
    }

    /// <summary>Resumes a past session in a new tab, with its earlier conversation loaded.</summary>
    public async Task OpenAsync(HistoryEntry entry)
    {
        if (!entry.IsConflictCopy && _host.AllTabs.FirstOrDefault(t => t.State.SessionId == entry.SessionId) is { } open)
        {
            _host.SelectTab(open);
            return;
        }
        if (entry.IsConflictCopy)
        {
            if (entry.Record is not null)
            {
                await OpenFromLibraryAsync(entry, fork: true, takeOver: false);
            }
            return;
        }
        // This machine's copy, unless another machine carried the session on since: then the library's is newer.
        var fromLibrary = !entry.IsLocal || entry.ContinuedElsewhere;
        if (fromLibrary && entry.Record is null)
        {
            return;
        }
        Func<bool, bool, Task> resume = fromLibrary
            ? (fork, takeOver) => OpenFromLibraryAsync(entry, fork, takeOver)
            : (fork, takeOver) => OpenLocalAsync(entry, fork, takeOver);
        // One machine at a time (DESIGN.md §9), whichever copy it opens from.
        if (entry.Record is not null && _services.Library.CheckLease(entry.SessionId) is LeaseStatus.HeldByOther other)
        {
            _host.Confirm(
                $"\"{entry.Title}\" is open on {other.Machine}",
                $"It was last active there at {other.UpdatedAt.ToLocalTime():t}. Open a copy to continue separately, or take it over; the tab on {other.Machine} then becomes read-only.",
                "Take over",
                () => resume(false, true),
                "Open a copy",
                () => resume(true, false));
            return;
        }
        await resume(false, false);
    }

    /// <summary>A session whose transcript is on this machine: resumes it where Claude Code keeps it.</summary>
    private async Task OpenLocalAsync(HistoryEntry entry, bool fork, bool takeOver)
    {
        var folder = entry.Folder is { } known && Directory.Exists(known)
            ? known
            : await _services.Platform.PickFolderAsync($"Choose the folder for \"{entry.Title}\"");
        if (folder is null)
        {
            return;
        }
        if (takeOver)
        {
            var library = _services.Library;
            await Task.Run(() => library.Leases.TakeOver(entry.SessionId, library.Library.GetSessionFolder(entry.SessionId)));
        }
        _host.OpenSession(NewState(entry, folder, transcriptPath: null, fork, _services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp));
    }

    /// <summary>
    /// A session from the library, possibly recorded on another machine (DESIGN.md §9, "Restoring on another machine"):
    /// find the project here, warn if the code differs, then copy the transcript to a local working copy and resume it.
    /// </summary>
    private async Task OpenFromLibraryAsync(HistoryEntry entry, bool fork, bool takeOver)
    {
        var record = entry.Record!;
        var folder = await FindProjectFolderAsync(record, entry.Title);
        if (folder is null)
        {
            return;
        }
        if (record.Project is { } recorded)
        {
            var local = ProjectIdentity.Read(folder);
            var difference = ProjectIdentity.Compare(recorded, record.HadUncommittedChanges, local);
            if (difference.Any)
            {
                _host.Confirm(
                    "The code here may be different",
                    difference.Describe(record.Machine, recorded, local) + " Sync the code first (push and pull), or continue anyway.",
                    "Continue anyway",
                    () => ResumeFromLibraryAsync(entry, folder, fork, takeOver));
                return;
            }
        }
        await ResumeFromLibraryAsync(entry, folder, fork, takeOver);
    }

    private async Task ResumeFromLibraryAsync(HistoryEntry entry, string folder, bool fork, bool takeOver)
    {
        var library = _services.Library;
        string transcript;
        try
        {
            if (entry.IsConflictCopy && entry.LibraryTranscript is { } copy)
            {
                // A conflict copy gets its own folder, so it can't overwrite the working copy of the original.
                var separate = Path.Combine(_services.Paths.LocalSessionsDirectory, $"copy-{Guid.NewGuid():N}");
                Directory.CreateDirectory(separate);
                transcript = Path.Combine(separate, $"{entry.SessionId}.jsonl");
                await Task.Run(() => File.Copy(copy, transcript, overwrite: true));
            }
            else
            {
                transcript = await library.CopyToLocalAsync(entry.SessionId);
            }
        }
        catch (Exception ex)
        {
            _host.Confirm("Couldn't open the session", ex.Message, "OK", () => Task.CompletedTask);
            return;
        }
        if (takeOver)
        {
            await Task.Run(() => library.Leases.TakeOver(entry.SessionId, library.Library.GetSessionFolder(entry.SessionId)));
        }
        _host.OpenSession(NewState(entry, folder, transcript, fork, _services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp));
    }

    /// <summary>
    /// Where a project lives on this machine: a folder remembered for it, a recent or favorite folder in the same
    /// repository, the recorded path if it exists here, or else the user picks one, which is remembered.
    /// </summary>
    private async Task<string?> FindProjectFolderAsync(SessionRecord record, string title)
    {
        var state = _services.State;
        var key = record.Project is { RemoteUrl: { } remote } project ? $"{ProjectIdentity.NormalizeRemote(remote)}|{project.PathInRepo}" : null;
        if (key is not null && state.FolderMappings.TryGetValue(key, out var mapped) && Directory.Exists(mapped))
        {
            return mapped;
        }
        if (record.Project is { RemoteUrl: not null } identity)
        {
            var candidates = state.FavoriteFolders.Concat(state.RecentFolders.Select(r => r.Path)).Concat(_host.AllTabs.Select(t => t.Folder)).Distinct();
            if (ProjectIdentity.FindMatchingFolder(identity, candidates) is { } match)
            {
                return match;
            }
        }
        if (record.Folder is { } recordedFolder && Directory.Exists(recordedFolder))
        {
            return recordedFolder;
        }
        var picked = await _services.Platform.PickFolderAsync($"Where is the folder for \"{title}\" on this machine?");
        if (picked is not null && key is not null)
        {
            state.FolderMappings[key] = picked;
            _services.SaveState();
        }
        return picked;
    }

    /// <param name="remoteControl">Settings → Claude Code → <b>Connect new tabs to the Claude app</b> (DESIGN.md §18).</param>
    private static TabState NewState(HistoryEntry entry, string folder, string? transcriptPath, bool fork, bool remoteControl)
    {
        var record = entry.Record;
        var state = new TabState
        {
            Folder = FolderHistory.Normalize(folder),
            SessionId = entry.SessionId,
            AutoName = record?.AutoName ?? entry.Title,
            UserName = fork ? null : record?.UserName,
            // The session's mark (DESIGN.md §4, "Marks"); a copy is a session of its own, so starts without, as without the name.
            Mark = fork ? null : entry.MarkKey,
            TranscriptPath = transcriptPath,
            ForkOnNextStart = fork,
            // A session someone synced keeps syncing wherever it's opened, a copy of one too; one that only ever lived
            // on this machine stays here (DESIGN.md §9, "Session library").
            SyncToLibrary = record is not null,
            // A tab opened from History is a new tab here; the phone connection belongs to this machine, not the record.
            RemoteControl = remoteControl,
        };
        if (record is not null)
        {
            // The tab's own choices come back with it (DESIGN.md §9); older records only had its model and effort.
            state.Overrides = record.Overrides is { } overrides
                ? JsonSerializer.Deserialize<TabOverrides>(JsonSerializer.Serialize(overrides, JsonFileStore<TabOverrides>.Options), JsonFileStore<TabOverrides>.Options) ?? new TabOverrides()
                : new TabOverrides { Model = record.Model, Effort = record.Effort };
            if (!fork)
            {
                state.Tokens = record.Tokens;
            }
            // Found in this machine's copy of the folder; a copy has the same changes, so keeps them too (DESIGN.md §8).
            state.ReviewedFiles = ReviewedFiles.FromRecord(record.ReviewedFiles, state.Folder);
        }
        return state;
    }
}
