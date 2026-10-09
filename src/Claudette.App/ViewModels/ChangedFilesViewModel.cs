using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Claudette.App.Conversation;
using Claudette.App.Diffs;
using Claudette.App.Services;
using Claudette.Core.Diffs;
using Claudette.Core.Git;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One file in the Changed files panel (DESIGN.md §8).</summary>
public sealed partial class ChangedFileRow : ObservableObject
{
    public required string Path { get; init; }

    public required string DisplayPath { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>A, M, D or R.</summary>
    public required string Status { get; init; }

    public required string StatusText { get; init; }

    public string? Stats { get; init; }

    public bool IsAdded => Status is "A" or "?";

    public bool IsDeleted => Status == "D";

    /// <summary>
    /// The "before" side: the file before Claude's first change, or null for a new file or when it's unknown. Git rows
    /// load it from HEAD.
    /// </summary>
    public string? Before { get; init; }

    /// <summary>False when what the file held before Claude's first change isn't known (DESIGN.md §8, "Before content").</summary>
    public bool BeforeKnown { get; init; } = true;

    public bool FromGit { get; init; }

    /// <summary>
    /// The id of Claude's latest change to the file when the row was made, or null when Claude hasn't changed it: what
    /// the row's checkbox marks as reviewed.
    /// </summary>
    public string? LatestChange { get; set; }

    /// <summary>The user marked the file as reviewed, and Claude hasn't changed it since (DESIGN.md §8, "Reviewed").</summary>
    [ObservableProperty]
    public partial bool IsReviewed { get; set; }

    /// <summary>Shows the same as <paramref name="other"/>, so the list can keep this row.</summary>
    public bool SameAs(ChangedFileRow other) =>
        Path == other.Path && DisplayPath == other.DisplayPath && Status == other.Status && StatusText == other.StatusText
        && Stats == other.Stats && ReferenceEquals(Before, other.Before) && BeforeKnown == other.BeforeKnown && FromGit == other.FromGit
        && LatestChange == other.LatestChange && IsReviewed == other.IsReviewed;
}

/// <summary>What the changed files panel needs from its tab.</summary>
internal interface IChangedFilesHost : ITabAreaHost
{
    /// <summary>The tab can copy its session to the library now (DESIGN.md §9, "Writing").</summary>
    bool CanSyncNow { get; }

    /// <summary>The tab is the one showing: a background tab's rows wait until it is (DESIGN.md §8).</summary>
    bool IsSelected { get; }

    /// <summary>Copies the session to the library in the background, for a tab that syncs.</summary>
    void CopyToLibrary();

    /// <summary><see cref="ChangedFilesViewModel.AllReviewed"/> changed: the tab's row shows the reviewed icon, or its mark again.</summary>
    void AllReviewedChanged();
}

/// <summary>The tab's changed files and diffs (DESIGN.md §8).</summary>
public sealed partial class ChangedFilesViewModel : ViewModelBase
{
    /// <summary>How long a tab that syncs waits after a tick before writing its record, so ticking several files writes it once.</summary>
    internal static readonly TimeSpan ReviewSyncDelay = TimeSpan.FromSeconds(2);

    private readonly AppServices _services;
    private readonly IChangedFilesHost _host;
    private ChangedFiles? _changes;
    private ReviewedFiles? _reviewed;
    /// <summary>Writes the library record once the ticking stops, for a tab that syncs.</summary>
    private UiTimeout ReviewSync => field ??= new(_services.Time, _services.Dispatcher);
    private bool _reviewSyncStopped;
    private bool _refreshQueued;
    /// <summary>Changes came while the tab was in the background; the rows catch up when it's selected.</summary>
    private bool _stale;

    /// <summary>
    /// Each changed file's inspection (reading it and diffing it against its "before"), kept until the file on disk or
    /// what's known of its "before" changes, so a refresh after one edit doesn't read and diff every file again.
    /// </summary>
    private readonly ConcurrentDictionary<string, (InspectionKey Key, ChangedFileState State)> _inspections = new(StringComparer.Ordinal);

    private readonly record struct InspectionKey(bool Exists, long Length, DateTime LastWriteUtc, string? Before, bool BeforeKnown, bool IsNew);
    /// <summary>Counts refreshes started, so one that finishes after a later one leaves that one's newer rows alone.</summary>
    private int _refreshGeneration;

    /// <summary>
    /// How long git mode waits after a change before running git: an edit at a time, a turn's dozen edits would each
    /// start their own <c>git status</c> and <c>git diff</c>.
    /// </summary>
    internal static readonly TimeSpan GitRefreshDelay = TimeSpan.FromMilliseconds(500);

    private UiTimeout GitRefreshWait => field ??= new(_services.Time, _services.Dispatcher);

    /// <summary>The git listing under way; a newer refresh stops it.</summary>
    private CancellationTokenSource? _gitRefresh;

    internal ChangedFilesViewModel(AppServices services, IChangedFilesHost host)
    {
        _services = services;
        _host = host;
        // As saved, until the session's changes are read back (ChangesRead).
        AllReviewed = host.State.AllFilesReviewed;
    }

    private ChangedFiles Changes
    {
        get
        {
            if (_changes is null)
            {
                _changes = new ChangedFiles(_services.Time) { Befores = new BeforeContentStore(_services.Paths.BeforeContentDirectory, _services.Time) };
                _changes.Changed += QueueRefresh;
                _changes.Changed += QueueAllReviewed;
            }
            return _changes;
        }
    }

    /// <summary>The files marked as reviewed, kept in the tab's state (DESIGN.md §8, "Reviewed").</summary>
    private ReviewedFiles Reviewed => _reviewed ??= new ReviewedFiles(_host.State.ReviewedFiles);

    public ObservableCollection<ChangedFileRow> Files { get; } = [];

    /// <summary>For example "5 files changed · 2 reviewed".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "No changes yet";

    /// <summary>"Working tree vs HEAD": all changes in the repository, including ones made by commands or by you.</summary>
    [ObservableProperty]
    public partial bool ShowGitChanges { get; set; }

    partial void OnShowGitChangesChanged(bool value) => _ = RefreshAsync();

    public bool IsGitRepository => GitInfo.TryGetBranch(_host.Folder) is not null;

    /// <summary>The tab moved to another folder, which may or may not be a repository.</summary>
    internal void OnFolderChanged() => OnPropertyChanged(nameof(IsGitRepository));

    /// <summary>Follows the session's Edit and Write calls, live and from a restored transcript.</summary>
    /// <param name="live">From the running session, so its changes count toward the turn's footer; false for a transcript read back.</param>
    internal void Record(SessionEvent sessionEvent, bool live = true)
    {
        switch (sessionEvent)
        {
            case AssistantMessageReceived { Message: var message }:
                foreach (var toolUse in message.Content.OfType<ToolUseBlock>().Where(t => ChangedFiles.IsFileTool(t.Name)))
                {
                    Changes.RecordToolUse(toolUse.Id, toolUse.Name, toolUse.Input);
                }
                break;
            case ToolResultsReceived { Message: var results }:
                foreach (var result in results.Content.OfType<ToolResultBlock>())
                {
                    Changes.RecordToolResult(result.ToolUseId, result.IsError, results.ToolUseResult, inTurn: live);
                }
                break;
        }
    }

    /// <summary>
    /// The conversation went back to an earlier point (DESIGN.md §5, "Rewind and branch"): the changes are recorded
    /// again as it's read back up to there.
    /// </summary>
    internal void Reset()
    {
        if (_changes is not null)
        {
            _changes.Changed -= QueueRefresh;
            _changes.Changed -= QueueAllReviewed;
            _changes = null;
        }
        _inspections.Clear();
        // The footers are read back without their files: a transcript has no turns to show.
        _turns.Clear();
        QueueRefresh();
        QueueAllReviewed();
    }

    // ---- Changes per turn (DESIGN.md §8) ---------------------------------------------------------------------------

    /// <summary>The footers of the turns that changed files, oldest first: a turn's changes end where a later one's start.</summary>
    private readonly List<TurnSummaryItem> _turns = [];

    /// <summary>
    /// A turn ended: the files it changed go on its footer, <paramref name="summary"/>, and the next turn starts
    /// counting. A turn without a footer (stopped, or ended in an error) keeps its changes only in the list.
    /// </summary>
    internal void EndTurn(TurnSummaryItem? summary)
    {
        var changes = Changes.TakeTurn();
        if (summary is null || changes.Count == 0)
        {
            return;
        }
        var folder = _host.Folder;
        var rows = changes.Select(c => new TurnFileRow(summary, c, Relative(c.Path, folder))).ToArray();
        summary.Files = rows;
        _turns.Add(summary);
        _ = CountTurnAsync(rows);
    }

    /// <summary>The footer's counts, against the files as the turn left them: on disk, as it has just ended.</summary>
    private async Task CountTurnAsync(IReadOnlyList<TurnFileRow> rows)
    {
        var changes = Changes;
        var counted = await Task.Run(() => rows
            .Select(row => changes.CountAgainstDisk(row.Change) is var (added, removed) ? $"+{added} −{removed}" : null)
            .ToArray());
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Stats = counted[i];
        }
    }

    /// <summary>
    /// A file on a turn's footer: its diff from before the turn to how the turn left it, which is how the next turn that
    /// changed it found it, or the file now when none has.
    /// </summary>
    [RelayCommand]
    private void OpenTurnDiff(TurnFileRow? row)
    {
        if (row is null)
        {
            return;
        }
        var change = row.Change;
        var later = _turns.SkipWhile(t => !ReferenceEquals(t, row.Turn)).Skip(1)
            .SelectMany(t => t.Files).FirstOrDefault(f => Changes.SamePath(f.Change.Path, change.Path));
        // A later turn's "before" that isn't known leaves the file now as the best there is.
        var after = later is { Change.BeforeKnown: true } ? new DiffAfter(later.Change.Before) : null;
        var label = !change.BeforeKnown ? "what it held before this turn isn't known, so there's nothing to compare it with"
            : after is not null ? (change.IsNew ? "this turn's changes: new in it, as it left it" : "this turn's changes: before it, against how it left it")
            : later is not null ? "this turn's changes, with a later turn's: before it, against the file now"
            : change.IsNew ? "this turn's changes: new in it, against the file now"
            : "this turn's changes: before it, against the file now";
        var file = new ChangedFileRow { Path = change.Path, DisplayPath = row.DisplayPath, Status = change.IsNew ? "A" : "M", StatusText = "" };
        DiffRequested?.Invoke(new DiffSource(
            change.Path,
            row.DisplayPath,
            change.Before,
            label,
            null,
            () => OpenFileInEditorAsync(file),
            () => RevealFileAsync(file),
            () => CopyFilePathAsync(file),
            change.BeforeKnown,
            After: after,
            AllowRevert: false));
    }

    private void QueueRefresh()
    {
        if (!_host.IsSelected)
        {
            // Nobody sees a background tab's rows: keep the count, and inspect the files (or ask git) once it's shown.
            _stale = true;
            if (!ShowGitChanges)
            {
                Count = Changes.Files.Count;
            }
            return;
        }
        if (ShowGitChanges)
        {
            // Once the edits pause.
            GitRefreshWait.Restart(GitRefreshDelay, () => _ = RefreshAsync());
            return;
        }
        if (_refreshQueued)
        {
            return;
        }
        _refreshQueued = true;
        _services.Dispatcher.Post(() =>
        {
            _refreshQueued = false;
            _ = RefreshAsync();
        });
    }

    [RelayCommand]
    internal async Task RefreshAsync()
    {
        var folder = _host.Folder;
        var generation = ++_refreshGeneration;
        List<ChangedFileRow> rows;
        if (ShowGitChanges)
        {
            GitRefreshWait.Cancel();
            var refresh = new CancellationTokenSource();
            Interlocked.Exchange(ref _gitRefresh, refresh)?.Cancel();
            IReadOnlyList<GitChange> changes;
            try
            {
                changes = await _services.Git.GetChangesAsync(folder, refresh.Token);
            }
            catch (OperationCanceledException)
            {
                // A newer refresh took over.
                return;
            }
            rows = changes.Select(c => new ChangedFileRow
            {
                Path = c.Path,
                DisplayPath = Relative(c.Path, folder),
                Status = c.Kind switch { GitChangeKind.Added or GitChangeKind.Untracked => "A", GitChangeKind.Deleted => "D", GitChangeKind.Renamed => "R", _ => "M" },
                StatusText = c.Kind switch
                {
                    GitChangeKind.Untracked => "Untracked",
                    GitChangeKind.Renamed => $"Renamed from {Relative(c.OldPath ?? "", folder)}",
                    _ => c.Kind.ToString(),
                },
                Stats = c.Added is { } a && c.Removed is { } r ? $"+{a} −{r}" : null,
                FromGit = true,
            }).ToList();
        }
        else
        {
            var files = Changes.Files.ToArray();
            var changes = Changes;
            rows = await Task.Run(() => files.Select(file =>
            {
                var state = Inspect(changes, file);
                return new ChangedFileRow
                {
                    Path = file.Path,
                    DisplayPath = changes.DisplayPath(file, folder),
                    Status = state.Status switch { ChangedFileStatus.Added => "A", ChangedFileStatus.Deleted => "D", ChangedFileStatus.Unchanged => "=", _ => "M" },
                    StatusText = state.Status switch
                    {
                        ChangedFileStatus.Unchanged => "Back to how it was",
                        _ when !file.BeforeKnown => $"{state.Status} (Claude Code didn't report the earlier content)",
                        _ => state.Status.ToString(),
                    },
                    Stats = state.Added is { } a && state.Removed is { } r ? $"+{a} −{r}" : state.IsBinary ? "binary" : null,
                    Before = file.Before,
                    BeforeKnown = file.BeforeKnown,
                };
            }).ToList());
        }
        if (generation != _refreshGeneration)
        {
            return;
        }
        // Marks on files Claude has changed since are forgotten (DESIGN.md §8, "Reviewed").
        if (Reviewed.Prune(Changes))
        {
            _services.SaveState();
        }
        foreach (var row in rows)
        {
            var claudeChanges = Changes.Find(row.Path);
            row.LatestChange = ReviewedFiles.LatestChange(claudeChanges);
            row.IsReviewed = Reviewed.IsReviewed(row.Path, claudeChanges);
        }
        _stale = false;
        SyncRows(rows);
        Count = Files.Count;
        UpdateSummary();
        OnPropertyChanged(nameof(IsGitRepository));
    }

    /// <summary>"Files (n)" in the composer bar, kept up to date even while the rows wait for the tab to be shown.</summary>
    [ObservableProperty]
    public partial int Count { get; private set; }

    /// <summary>The tab was selected: rows that waited while it was in the background catch up.</summary>
    internal void RefreshIfStale()
    {
        if (_stale)
        {
            QueueRefresh();
        }
    }

    /// <summary>A file's inspection, from the cache while neither the file nor what's known of it has changed.</summary>
    private ChangedFileState Inspect(ChangedFiles changes, ChangedFile file)
    {
        var info = new FileInfo(file.Path);
        var key = info.Exists
            ? new InspectionKey(true, info.Length, info.LastWriteTimeUtc, file.Before, file.BeforeKnown, file.IsNew)
            : new InspectionKey(false, 0, default, file.Before, file.BeforeKnown, file.IsNew);
        if (_inspections.TryGetValue(file.Path, out var cached) && cached.Key == key)
        {
            return cached.State;
        }
        // The rows don't need the file's text, so the cache doesn't keep it.
        var state = changes.Inspect(file) with { Current = null };
        _inspections[file.Path] = (key, state);
        return state;
    }

    /// <summary>
    /// Brings the list in line with <paramref name="rows"/>, replacing only the rows that changed, rather than clearing
    /// and refilling it (which rebuilt every row's controls after each edit).
    /// </summary>
    private void SyncRows(List<ChangedFileRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= Files.Count)
            {
                Files.Add(rows[i]);
            }
            else if (!Files[i].SameAs(rows[i]))
            {
                Files[i] = rows[i];
            }
        }
        while (Files.Count > rows.Count)
        {
            Files.RemoveAt(Files.Count - 1);
        }
    }

    /// <summary>For example "5 files changed · 2 reviewed".</summary>
    private void UpdateSummary()
    {
        var summary = Files.Count switch
        {
            0 => ShowGitChanges ? "No changes in the working tree" : "No changes yet",
            1 => "1 file changed",
            var count => $"{count} files changed",
        };
        var reviewed = Files.Count(r => r.IsReviewed);
        Summary = reviewed > 0 ? $"{summary} · {reviewed} reviewed" : summary;
    }

    private static string Relative(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return (relative.StartsWith("..", StringComparison.Ordinal) ? path : relative).Replace('\\', '/');
    }

    // ---- Reviewed (DESIGN.md §8) -----------------------------------------------------------------------------------

    /// <summary>A row's checkbox: marks the file as reviewed, or clears the mark.</summary>
    [RelayCommand]
    private void ToggleFileReviewed(ChangedFileRow? row)
    {
        if (row is not null)
        {
            SetFileReviewed(row.Path, !row.IsReviewed, row.LatestChange);
        }
    }

    /// <summary>
    /// Marks a file as reviewed as of Claude's change <paramref name="change"/> to it, or clears its mark. It stays
    /// reviewed until Claude changes it again, so marking a change older than Claude's latest leaves it unreviewed.
    /// </summary>
    private void SetFileReviewed(string path, bool reviewed, string? change)
    {
        if (reviewed)
        {
            Reviewed.Mark(path, change);
        }
        else
        {
            Reviewed.Unmark(path);
        }
        foreach (var row in Files)
        {
            row.IsReviewed = Reviewed.IsReviewed(row.Path, Changes.Find(row.Path));
        }
        UpdateSummary();
        UpdateAllReviewed();
        _services.SaveState();
        QueueReviewSync();
    }

    /// <summary>
    /// Every file Claude changed in the session is ticked as reviewed, and there's at least one: the tab's row shows the
    /// reviewed icon in place of its mark (DESIGN.md §4, "Marks"). Files only you or commands changed, in working tree
    /// vs HEAD, don't count, as they don't untick either. Kept up to date in the background too, from Claude's changes
    /// alone, so it doesn't wait for the rows. Saved with the tab, so a restored tab has it before it reads its
    /// conversation back.
    /// </summary>
    [ObservableProperty]
    public partial bool AllReviewed { get; private set; }

    partial void OnAllReviewedChanged(bool value)
    {
        if (_host.State.AllFilesReviewed != value)
        {
            _host.State.AllFilesReviewed = value;
            _services.SaveState();
        }
        _host.AllReviewedChanged();
    }

    /// <summary>
    /// The tab has read its earlier conversation back, or found none: <see cref="AllReviewed"/> goes by the changes
    /// from now on, rather than by what was saved.
    /// </summary>
    internal void ChangesRead() => UpdateAllReviewed();

    private bool _allReviewedQueued;

    /// <summary>
    /// Works out <see cref="AllReviewed"/> again once the changes recorded together are in, rather than after each: a
    /// transcript read back records them by the thousand.
    /// </summary>
    private void QueueAllReviewed()
    {
        if (_allReviewedQueued)
        {
            return;
        }
        _allReviewedQueued = true;
        _services.Dispatcher.Post(() =>
        {
            _allReviewedQueued = false;
            UpdateAllReviewed();
        });
    }

    private void UpdateAllReviewed()
    {
        var files = Changes.Files;
        AllReviewed = files.Count > 0 && files.All(file => Reviewed.IsReviewed(file.Path, file));
    }

    /// <summary>
    /// Brings the library's record up to date with the marks, for a tab that syncs (DESIGN.md §9, "Writing"). The
    /// transcript is only copied again if it changed. While a turn runs, the copy as it ends carries them instead.
    /// </summary>
    private void QueueReviewSync()
    {
        if (!_host.State.SyncToLibrary || _reviewSyncStopped)
        {
            return;
        }
        ReviewSync.Restart(ReviewSyncDelay, () =>
        {
            if (_host.CanSyncNow && !_reviewSyncStopped)
            {
                _host.CopyToLibrary();
            }
        });
    }

    /// <summary>
    /// The tab is closing: a diff view still open can mark files, but nothing more is written to the library, and git
    /// isn't asked again.
    /// </summary>
    internal void StopReviewSync()
    {
        _reviewSyncStopped = true;
        ReviewSync.Cancel();
        GitRefreshWait.Cancel();
        _gitRefresh?.Cancel();
    }

    // ---- Opening diffs (DESIGN.md §8) ------------------------------------------------------------------------------

    /// <summary>Asks the view to show the built-in diff view.</summary>
    public event Action<DiffSource>? DiffRequested;

    /// <summary>Whether an external diff tool is set in Settings → Diff tool.</summary>
    public bool HasDiffTool => DiffTool.Kind != DiffToolKind.BuiltIn;

    private DiffToolChoice DiffTool
    {
        get
        {
            var settings = _services.Settings.DiffTool;
            return settings.Kind switch
            {
                "preset" when settings.PresetId is { Length: > 0 } => new DiffToolChoice(DiffToolKind.Preset, settings.PresetId),
                "custom" when settings.CustomCommand is { Length: > 0 } => new DiffToolChoice(DiffToolKind.Custom, CustomCommand: settings.CustomCommand),
                _ => DiffToolChoice.BuiltIn,
            };
        }
    }

    private string DiffTempDirectory => Path.Combine(_services.Paths.DiffTempDirectory, _host.Id);

    /// <summary>Selecting a file opens the built-in diff view.</summary>
    [RelayCommand]
    private async Task OpenFileDiffAsync(ChangedFileRow? row)
    {
        if (row is null)
        {
            return;
        }
        var before = row.FromGit ? await _services.Git.GetHeadContentAsync(_host.Folder, row.Path) : row.Before;
        var beforeKnown = row.FromGit || row.BeforeKnown;
        DiffRequested?.Invoke(new DiffSource(
            row.Path,
            row.DisplayPath,
            before,
            row.FromGit ? "compared with HEAD"
                : !beforeKnown ? "what it held before Claude's first change isn't known, so there's nothing to compare it with"
                : before is null ? "new in this session"
                : "compared with before Claude's first change in this session",
            HasDiffTool && beforeKnown ? () => OpenFileInDiffToolAsync(row) : null,
            () => OpenFileInEditorAsync(row),
            () => RevealFileAsync(row),
            () => CopyFilePathAsync(row),
            beforeKnown,
            new DiffReview(
                () => ReviewedFiles.LatestChange(Changes.Find(row.Path)),
                () => Reviewed.IsReviewed(row.Path, Changes.Find(row.Path)),
                change => SetFileReviewed(row.Path, reviewed: true, change)),
            FromGit: row.FromGit));
    }

    /// <summary>
    /// <b>Open diff</b> on an Edit or Write card (DESIGN.md §5): the file in the diff view, as Changed files shows it,
    /// from before Claude's first change in this session to the file now.
    /// </summary>
    [RelayCommand]
    private Task OpenToolDiffAsync(ToolUseItem? tool) => tool is null ? Task.CompletedTask : OpenChangeDiffAsync(tool.ToolUseId);

    /// <summary>The file a change (its tool call's id) was made to, in the diff view as Changed files shows it.</summary>
    internal Task OpenChangeDiffAsync(string toolUseId)
    {
        if (Changes.Files.FirstOrDefault(f => f.ToolUseIds.Contains(toolUseId)) is not { } file)
        {
            return Task.CompletedTask;
        }
        return OpenFileDiffAsync(new ChangedFileRow
        {
            Path = file.Path,
            DisplayPath = Changes.DisplayPath(file, _host.Folder),
            Status = file.IsNew ? "A" : "M",
            StatusText = file.IsNew ? "Added" : "Modified",
            Before = file.Before,
            BeforeKnown = file.BeforeKnown,
        });
    }

    /// <summary>Double-click: the external diff tool when one is set, else the built-in view.</summary>
    [RelayCommand]
    private Task OpenFileAsync(ChangedFileRow? row) => HasDiffTool ? OpenFileInDiffToolAsync(row) : OpenFileDiffAsync(row);

    /// <summary>A file whose "before" is unknown opens in the built-in view instead, which says so.</summary>
    [RelayCommand]
    private async Task OpenFileInDiffToolAsync(ChangedFileRow? row)
    {
        if (row is null || !HasDiffTool)
        {
            return;
        }
        if (!row.FromGit && !row.BeforeKnown)
        {
            await OpenFileDiffAsync(row);
            return;
        }
        try
        {
            var before = row.FromGit ? await _services.Git.GetHeadContentAsync(_host.Folder, row.Path) : row.Before;
            await new DiffToolLauncher(_services.Launcher, _services.Time, environment: _services.UserEnvironment).LaunchAsync(DiffTool, before, row.Path, DiffTempDirectory);
        }
        catch (Exception ex)
        {
            _host.AddNote($"Couldn't open the diff tool: {ex.Message}", NoteKind.Error);
        }
    }

    [RelayCommand]
    private Task OpenFileInEditorAsync(ChangedFileRow? row) =>
        row is { IsDeleted: false } ? _services.Platform.OpenFileAsync(row.Path) : Task.CompletedTask;

    [RelayCommand]
    private Task RevealFileAsync(ChangedFileRow? row) =>
        row is not null && Path.GetDirectoryName(row.Path) is { } folder && Directory.Exists(folder)
            ? _services.Platform.RevealFolderAsync(folder)
            : Task.CompletedTask;

    [RelayCommand]
    private Task CopyFilePathAsync(ChangedFileRow? row) =>
        row is null ? Task.CompletedTask : _services.Platform.SetClipboardTextAsync(row.Path);

    /// <summary>The "before" files handed to diff tools are deleted when the tab closes (DESIGN.md §8).</summary>
    internal void CleanUpDiffFiles()
    {
        try
        {
            DiffTempFiles.Cleanup(DiffTempDirectory);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
