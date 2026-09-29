using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.History;
using Claudette.Core.Library;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One past session in History.</summary>
public sealed class HistoryEntry
{
    public required string SessionId { get; init; }

    public required string Title { get; init; }

    public string? FirstPrompt { get; init; }

    public bool HasFirstPrompt => !string.IsNullOrEmpty(FirstPrompt) && FirstPrompt != Title;

    /// <summary>The session's folder: on this machine for local sessions, on the other machine for library-only ones.</summary>
    public string? Folder { get; init; }

    /// <summary>"This machine", or the machine that last used it.</summary>
    public required string Machine { get; init; }

    public DateTimeOffset LastActivity { get; init; }

    public int MessageCount { get; init; }

    public string? Branch { get; init; }

    /// <summary>Claude Code's transcript on this machine, if there is one.</summary>
    public string? LocalTranscript { get; init; }

    /// <summary>The session's library record, if it's in the library.</summary>
    public SessionRecord? Record { get; init; }

    /// <summary>The library's transcript (for a conflict copy, that copy).</summary>
    public string? LibraryTranscript { get; init; }

    /// <summary>A copy a sync client made when two machines changed the session at once. Opens as a fork.</summary>
    public bool IsConflictCopy { get; init; }

    public string? ConflictLabel { get; init; }

    public bool IsOpen { get; set; }

    public bool IsLocal => LocalTranscript is not null;

    public required string Details { get; init; }
}

/// <summary>A folder in History, with its sessions.</summary>
public sealed record HistoryGroup(string Label, string? Folder, IReadOnlyList<HistoryEntry> Entries);

/// <summary>
/// History (DESIGN.md §9): past sessions grouped by folder, from Claude Code's own storage on this machine (so it
/// includes terminal sessions) and from the session library (which can include other machines). Opening one resumes it
/// in a new tab.
/// </summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private IReadOnlyList<HistoryEntry> _all = [];

    public HistoryViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        _ = LoadAsync();
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => Filter();

    public ObservableCollection<HistoryGroup> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsEmpty => !IsLoading && Groups.Count == 0;

    [RelayCommand]
    private async Task OpenAsync(HistoryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }
        _shell.CloseHistory();
        await _shell.OpenFromHistoryAsync(entry);
    }

    [RelayCommand]
    private void Close() => _shell.CloseHistory();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        var library = _services.Library;
        var index = library.History;
        var machine = library.MachineName;
        var now = _services.Time.GetUtcNow();
        var open = _shell.AllTabs.Select(t => t.State.SessionId).OfType<string>().ToHashSet();
        try
        {
            _all = await Task.Run(async () =>
            {
                var local = index is null ? [] : await index.ScanAsync().ConfigureAwait(false);
                IReadOnlyList<LibraryEntry> stored;
                try
                {
                    stored = library.Library.List();
                }
                catch (Exception)
                {
                    stored = [];
                }
                return Merge(local, stored, machine, now, open);
            });
        }
        catch (Exception ex)
        {
            Error = $"Couldn't read the session history: {ex.Message}";
            _all = [];
        }
        // Fill the list before saying it's loaded, so nothing sees "loaded" with an empty list.
        Filter();
        IsLoading = false;
    }

    private static List<HistoryEntry> Merge(IReadOnlyList<SessionSummary> local, IReadOnlyList<LibraryEntry> stored, string machine, DateTimeOffset now, HashSet<string> open)
    {
        var records = stored.Where(e => !e.IsConflictCopy).GroupBy(e => e.Record.SessionId).ToDictionary(g => g.Key, g => g.First());
        var entries = new List<HistoryEntry>();
        var seen = new HashSet<string>();

        foreach (var summary in local)
        {
            seen.Add(summary.SessionId);
            records.TryGetValue(summary.SessionId, out var libraryEntry);
            var record = libraryEntry?.Record;
            var lastUsedElsewhere = record is not null && record.Machine != machine && record.LastUsed > summary.LastActivity;
            entries.Add(Entry(summary.SessionId, record?.Name ?? summary.Title, summary.FirstPrompt ?? record?.FirstPrompt, summary.Folder ?? record?.Folder,
                lastUsedElsewhere ? record!.Machine : machine, lastUsedElsewhere ? record!.LastUsed : summary.LastActivity,
                summary.MessageCount, summary.GitBranch, summary.TranscriptPath, record, libraryEntry?.TranscriptPath, isConflict: false, label: null));
        }

        foreach (var libraryEntry in stored)
        {
            var record = libraryEntry.Record;
            if (!libraryEntry.IsConflictCopy && seen.Contains(record.SessionId))
            {
                continue;
            }
            // A session only in the library, from another machine (or older than Claude Code's own cleanup).
            var summary = HistoryIndex.ReadSummary(libraryEntry.TranscriptPath);
            var title = record.Name ?? summary?.Title ?? summary?.FirstPrompt ?? "Untitled session";
            entries.Add(Entry(record.SessionId, libraryEntry.IsConflictCopy ? $"{title} ({libraryEntry.ConflictLabel})" : title,
                summary?.FirstPrompt ?? record.FirstPrompt, record.Folder, record.Machine, record.LastUsed,
                summary?.MessageCount ?? 0, record.Project?.Branch, localTranscript: null, record, libraryEntry.TranscriptPath,
                libraryEntry.IsConflictCopy, libraryEntry.ConflictLabel));
        }
        return entries.OrderByDescending(e => e.LastActivity).ToList();

        HistoryEntry Entry(string id, string? title, string? prompt, string? folder, string lastMachine, DateTimeOffset last, int messages, string? branch,
            string? localTranscript, SessionRecord? record, string? libraryTranscript, bool isConflict, string? label)
        {
            var who = lastMachine == machine ? "This machine" : lastMachine;
            var details = new List<string> { who, Ago(now - last) };
            if (messages > 0)
            {
                details.Add($"{messages} message{(messages == 1 ? "" : "s")}");
            }
            if (branch is not null)
            {
                details.Add(branch);
            }
            if (open.Contains(id))
            {
                details.Add("open in a tab");
            }
            return new HistoryEntry
            {
                SessionId = id,
                Title = title is { Length: > 0 } ? title : prompt ?? "Untitled session",
                FirstPrompt = prompt,
                Folder = folder,
                Machine = who,
                LastActivity = last,
                MessageCount = messages,
                Branch = branch,
                LocalTranscript = localTranscript,
                Record = record,
                LibraryTranscript = libraryTranscript,
                IsConflictCopy = isConflict,
                ConflictLabel = label,
                IsOpen = open.Contains(id),
                Details = string.Join(" · ", details),
            };
        }
    }

    /// <summary>Search by title and prompt text; grouped by folder, most recent first.</summary>
    private void Filter()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _all.Where(e => words.All(w =>
            e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
            || (e.FirstPrompt?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Folder?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)));
        Groups.Clear();
        foreach (var group in matches.GroupBy(e => e.Folder is null ? "" : FolderHistory.Normalize(e.Folder), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Max(e => e.LastActivity)))
        {
            var folder = group.First().Folder;
            var label = folder is null ? "Unknown folder" : Path.GetFileName(folder.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : folder;
            Groups.Add(new HistoryGroup(label, folder, group.ToArray()));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)span.TotalDays} days ago",
    };
}
