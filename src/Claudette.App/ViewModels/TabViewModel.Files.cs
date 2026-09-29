using System.Collections.ObjectModel;
using Claudette.App.Diffs;
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
}

/// <summary>The tab's changed files and diffs (DESIGN.md §8).</summary>
public sealed partial class TabViewModel
{
    private ChangedFiles? _changes;
    private ReviewedFiles? _reviewed;
    /// <summary>Writes the library record once the ticking stops, for a tab that syncs.</summary>
    private ITimer? _reviewSync;
    private bool _reviewSyncStopped;
    private bool _refreshQueued;

    private ChangedFiles Changes
    {
        get
        {
            if (_changes is null)
            {
                _changes = new ChangedFiles(_services.Time) { Befores = new BeforeContentStore(_services.Paths.BeforeContentDirectory, _services.Time) };
                _changes.Changed += QueueChangedFilesRefresh;
            }
            return _changes;
        }
    }

    /// <summary>The files marked as reviewed, kept in the tab's state (DESIGN.md §8, "Reviewed").</summary>
    private ReviewedFiles Reviewed => _reviewed ??= new ReviewedFiles(State.ReviewedFiles);

    /// <summary>
    /// The side panel: Changed files, Agents, Project when the tab has project tools, and Processes when the monitor is
    /// on (DESIGN.md §3).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProcessSummary))]
    public partial bool IsSidePanelOpen { get; set; }

    partial void OnIsSidePanelOpenChanged(bool value)
    {
        IsProcessPanelVisible = value && IsProcessesPage;
        if (value)
        {
            _ = RefreshChangedFilesAsync();
        }
        UpdateShownProjectRun();
    }

    /// <summary>Which page of the side panel shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesPage))]
    public partial bool IsProcessesPage { get; set; }

    public bool IsFilesPage => !IsProcessesPage && !IsAgentsPage && !IsProjectPage;

    partial void OnIsProcessesPageChanged(bool value)
    {
        IsProcessPanelVisible = value && IsSidePanelOpen;
        if (value)
        {
            IsAgentsPage = false;
            IsProjectPage = false;
        }
    }

    [RelayCommand]
    private void ToggleSidePanel() => IsSidePanelOpen = !IsSidePanelOpen;

    /// <summary>The side panel's width: the same for every tab, set by dragging its edge (DESIGN.md §3).</summary>
    public double SidePanelWidth => _shell.SidePanelWidth;

    /// <summary>Dragging the side panel's edge. <see cref="SaveSidePanelWidth"/> keeps the result when the drag ends.</summary>
    public void ResizeSidePanel(double width) => _shell.ResizeSidePanel(width);

    public void SaveSidePanelWidth() => _shell.SaveSidePanelWidth();

    /// <summary>Double-clicking the edge.</summary>
    public void ResetSidePanelWidth()
    {
        _shell.ResizeSidePanel(ShellViewModel.DefaultSidePanelWidth);
        _shell.SaveSidePanelWidth();
    }

    internal void OnSidePanelWidthChanged() => OnPropertyChanged(nameof(SidePanelWidth));

    [RelayCommand]
    private void ShowFilesPage()
    {
        IsProcessesPage = false;
        IsAgentsPage = false;
        IsProjectPage = false;
    }

    [RelayCommand]
    private void ShowProcessesPage() => IsProcessesPage = true;

    public ObservableCollection<ChangedFileRow> ChangedFiles { get; } = [];

    [ObservableProperty]
    public partial string ChangedFilesSummary { get; set; } = "No changes yet";

    /// <summary>"Working tree vs HEAD": all changes in the repository, including ones made by commands or by you.</summary>
    [ObservableProperty]
    public partial bool ShowGitChanges { get; set; }

    partial void OnShowGitChangesChanged(bool value) => _ = RefreshChangedFilesAsync();

    public bool IsGitRepository => GitInfo.TryGetBranch(Folder) is not null;

    /// <summary>Follows the session's Edit and Write calls, live and from a restored transcript.</summary>
    private void RecordFileChanges(SessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case AssistantMessageReceived { Message: var message }:
                foreach (var toolUse in message.Content.OfType<ToolUseBlock>().Where(t => Core.Diffs.ChangedFiles.IsFileTool(t.Name)))
                {
                    Changes.RecordToolUse(toolUse.Id, toolUse.Name, toolUse.Input);
                }
                break;
            case ToolResultsReceived { Message: var results }:
                foreach (var result in results.Content.OfType<ToolResultBlock>())
                {
                    Changes.RecordToolResult(result.ToolUseId, result.IsError, results.ToolUseResult);
                }
                break;
        }
    }

    private void QueueChangedFilesRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }
        _refreshQueued = true;
        _services.Dispatcher.Post(() =>
        {
            _refreshQueued = false;
            _ = RefreshChangedFilesAsync();
        });
    }

    [RelayCommand]
    private async Task RefreshChangedFilesAsync()
    {
        var folder = Folder;
        List<ChangedFileRow> rows;
        if (ShowGitChanges)
        {
            var changes = await _services.Git.GetChangesAsync(folder);
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
                var state = changes.Inspect(file);
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
        ChangedFiles.Clear();
        foreach (var row in rows)
        {
            ChangedFiles.Add(row);
        }
        UpdateChangedFilesSummary();
        OnPropertyChanged(nameof(IsGitRepository));
    }

    /// <summary>For example "5 files changed · 2 reviewed".</summary>
    private void UpdateChangedFilesSummary()
    {
        var summary = ChangedFiles.Count switch
        {
            0 => ShowGitChanges ? "No changes in the working tree" : "No changes yet",
            1 => "1 file changed",
            var count => $"{count} files changed",
        };
        var reviewed = ChangedFiles.Count(r => r.IsReviewed);
        ChangedFilesSummary = reviewed > 0 ? $"{summary} · {reviewed} reviewed" : summary;
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
        foreach (var row in ChangedFiles)
        {
            row.IsReviewed = Reviewed.IsReviewed(row.Path, Changes.Find(row.Path));
        }
        UpdateChangedFilesSummary();
        _services.SaveState();
        QueueReviewSync();
    }

    /// <summary>How long a tab that syncs waits after a tick before writing its record, so ticking several files writes it once.</summary>
    internal static readonly TimeSpan ReviewSyncDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Brings the library's record up to date with the marks, for a tab that syncs (DESIGN.md §9, "Writing"). The
    /// transcript is only copied again if it changed. While a turn runs, the copy as it ends carries them instead.
    /// </summary>
    private void QueueReviewSync()
    {
        if (!State.SyncToLibrary || _reviewSyncStopped)
        {
            return;
        }
        _reviewSync?.Dispose();
        ITimer? timer = null;
        timer = _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() =>
        {
            if (timer is null || !ReferenceEquals(_reviewSync, timer))
            {
                return;
            }
            _reviewSync = null;
            timer.Dispose();
            if (CanSyncNow && !_reviewSyncStopped)
            {
                CopyToLibrary();
            }
        }), null, ReviewSyncDelay, Timeout.InfiniteTimeSpan);
        _reviewSync = timer;
    }

    /// <summary>The tab is closing: a diff view still open can mark files, but nothing more is written to the library.</summary>
    private void StopReviewSync()
    {
        _reviewSyncStopped = true;
        _reviewSync?.Dispose();
        _reviewSync = null;
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

    private string DiffTempDirectory => Path.Combine(_services.Paths.DiffTempDirectory, Id);

    /// <summary>Selecting a file opens the built-in diff view.</summary>
    [RelayCommand]
    private async Task OpenFileDiffAsync(ChangedFileRow? row)
    {
        if (row is null)
        {
            return;
        }
        var before = row.FromGit ? await _services.Git.GetHeadContentAsync(Folder, row.Path) : row.Before;
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
                change => SetFileReviewed(row.Path, reviewed: true, change))));
    }

    /// <summary>
    /// <b>Open diff</b> on an Edit or Write card (DESIGN.md §5): the file in the diff view, as Changed files shows it,
    /// from before Claude's first change in this session to the file now.
    /// </summary>
    [RelayCommand]
    private Task OpenToolDiffAsync(Conversation.ToolUseItem? tool)
    {
        if (tool is null || Changes.Files.FirstOrDefault(f => f.ToolUseIds.Contains(tool.ToolUseId)) is not { } file)
        {
            return Task.CompletedTask;
        }
        return OpenFileDiffAsync(new ChangedFileRow
        {
            Path = file.Path,
            DisplayPath = Changes.DisplayPath(file, Folder),
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
            var before = row.FromGit ? await _services.Git.GetHeadContentAsync(Folder, row.Path) : row.Before;
            await new DiffToolLauncher(_services.Launcher, _services.Time, environment: _services.UserEnvironment).LaunchAsync(DiffTool, before, row.Path, DiffTempDirectory);
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't open the diff tool: {ex.Message}", Conversation.NoteKind.Error);
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
    private void CleanUpDiffFiles()
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
