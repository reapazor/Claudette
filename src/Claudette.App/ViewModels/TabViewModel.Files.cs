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
public sealed class ChangedFileRow
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

    /// <summary>The "before" side: Claude's first change's <c>originalFile</c>, or null for a new file. Git rows load it from HEAD.</summary>
    public string? Before { get; init; }

    public bool FromGit { get; init; }
}

/// <summary>The tab's changed files and diffs (DESIGN.md §8).</summary>
public sealed partial class TabViewModel
{
    private ChangedFiles? _changes;
    private bool _refreshQueued;

    private ChangedFiles Changes
    {
        get
        {
            if (_changes is null)
            {
                _changes = new ChangedFiles(_services.Time);
                _changes.Changed += QueueChangedFilesRefresh;
            }
            return _changes;
        }
    }

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
                };
            }).ToList());
        }
        ChangedFiles.Clear();
        foreach (var row in rows)
        {
            ChangedFiles.Add(row);
        }
        ChangedFilesSummary = rows.Count switch
        {
            0 => ShowGitChanges ? "No changes in the working tree" : "No changes yet",
            1 => "1 file changed",
            _ => $"{rows.Count} files changed",
        };
        OnPropertyChanged(nameof(IsGitRepository));
    }

    private static string Relative(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return (relative.StartsWith("..", StringComparison.Ordinal) ? path : relative).Replace('\\', '/');
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
        DiffRequested?.Invoke(new DiffSource(
            row.Path,
            row.DisplayPath,
            before,
            row.FromGit ? "compared with HEAD" : before is null ? "new in this session" : "compared with before Claude's first change in this session",
            HasDiffTool ? () => OpenFileInDiffToolAsync(row) : null,
            () => OpenFileInEditorAsync(row),
            () => RevealFileAsync(row),
            () => CopyFilePathAsync(row)));
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
        });
    }

    /// <summary>Double-click: the external diff tool when one is set, else the built-in view.</summary>
    [RelayCommand]
    private Task OpenFileAsync(ChangedFileRow? row) => HasDiffTool ? OpenFileInDiffToolAsync(row) : OpenFileDiffAsync(row);

    [RelayCommand]
    private async Task OpenFileInDiffToolAsync(ChangedFileRow? row)
    {
        if (row is null || !HasDiffTool)
        {
            return;
        }
        try
        {
            var before = row.FromGit ? await _services.Git.GetHeadContentAsync(Folder, row.Path) : row.Before;
            await new DiffToolLauncher(_services.Launcher, _services.Time).LaunchAsync(DiffTool, before, row.Path, DiffTempDirectory);
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
