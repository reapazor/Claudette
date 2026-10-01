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

    /// <summary>Every prompt in the session, for search (DESIGN.md §9, "Search by title and prompt text").</summary>
    public string? Prompts { get; init; }

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

    /// <summary>Where Claude's replies matched the search, when only they did (DESIGN.md §9, "History").</summary>
    public string? MatchedReply { get; set; }

    public bool HasMatchedReply => MatchedReply is not null;

    public bool IsLocal => LocalTranscript is not null;

    /// <summary>
    /// Another machine used it after this one's copy: the library's transcript is newer, so it opens from the library
    /// (DESIGN.md §9, "Merging the sources").
    /// </summary>
    public bool ContinuedElsewhere { get; init; }

    public required string Details { get; init; }
}

/// <summary>A folder in History, with its sessions.</summary>
public sealed record HistoryGroup(string Label, string? Folder, IReadOnlyList<HistoryEntry> Entries);

/// <summary>A folder's heading in History's list, above its sessions.</summary>
public sealed record HistoryHeading(string Label, string? Folder);

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
    [NotifyCanExecuteChangedFor(nameof(SearchRepliesCommand))]
    public partial string Search { get; set; } = "";

    /// <summary>How long typing pauses before History filters: a long one is thousands of sessions' prompts.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    private ITimer? _searchTimer;
    private int _filterVersion;
    private bool _replyFilterQueued;

    /// <summary>The latest filtering, for tests to wait on.</summary>
    internal Task Filtering { get; private set; } = Task.CompletedTask;

    /// <summary>A search is waiting for typing to pause, or being filtered.</summary>
    internal bool IsFilterPending => _searchTimer is not null || !Filtering.IsCompleted;

    partial void OnSearchChanged(string value)
    {
        // A new search: the replies' matches were for the old one.
        StopSearchingReplies();
        _searchTimer?.Dispose();
        _searchTimer = _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() =>
        {
            _searchTimer?.Dispose();
            _searchTimer = null;
            Filtering = FilterAsync();
        }), null, SearchDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Filters again once, however many replies were found meanwhile.</summary>
    private void QueueFilter()
    {
        if (_replyFilterQueued)
        {
            return;
        }
        _replyFilterQueued = true;
        _services.Dispatcher.Post(() =>
        {
            _replyFilterQueued = false;
            Filtering = FilterAsync();
        });
    }

    // ---- Search Claude's replies too ----------------------------------------------------------------------------

    private readonly Dictionary<HistoryEntry, string> _replyMatches = [];
    private CancellationTokenSource? _replySearch;

    /// <summary>Looking through transcripts for <see cref="SearchRepliesCommand"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchRepliesCommand))]
    public partial bool IsSearchingReplies { get; set; }

    /// <summary>"Searching Claude's replies…", then what it found; null before a search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplySearchText))]
    public partial string? ReplySearchText { get; set; }

    public bool HasReplySearchText => ReplySearchText is not null;

    private bool CanSearchReplies() => !IsSearchingReplies && !IsLoading && Words().Length > 0;

    /// <summary>
    /// Looks through Claude's replies in the sessions the search didn't match, reading each transcript (History keeps
    /// only prompts), and adds those whose replies have the words the rest lacks. Results appear as they're found.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearchReplies))]
    private async Task SearchRepliesAsync()
    {
        StopSearchingReplies();
        var words = Words();
        var candidates = _all
            .Where(e => !MatchesWithoutReplies(e, words) && (e.LocalTranscript ?? e.LibraryTranscript) is not null)
            .ToArray();
        var search = _replySearch = new CancellationTokenSource();
        IsSearchingReplies = true;
        ReplySearchText = $"Searching Claude's replies in {Sessions(candidates.Length)}…";
        try
        {
            var found = await Task.Run(() =>
            {
                var count = 0;
                foreach (var entry in candidates)
                {
                    search.Token.ThrowIfCancellationRequested();
                    var known = string.Join('\n', entry.Title, entry.FirstPrompt, entry.Prompts, entry.Folder);
                    if (ReplySearch.Find((entry.LocalTranscript ?? entry.LibraryTranscript)!, words, known, search.Token) is { } snippet)
                    {
                        count++;
                        _services.Dispatcher.Post(() =>
                        {
                            if (!search.IsCancellationRequested)
                            {
                                _replyMatches[entry] = snippet;
                                QueueFilter();
                            }
                        });
                    }
                }
                return count;
            }, search.Token);
            // A search that finished just as the words changed says nothing about the new ones.
            if (ReferenceEquals(_replySearch, search) && !search.IsCancellationRequested)
            {
                ReplySearchText = found == 0 ? "Claude's replies in the other sessions don't mention that either." : $"Found {Sessions(found)} more in Claude's replies.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_replySearch, search))
            {
                IsSearchingReplies = false;
            }
        }

        static string Sessions(int n) => $"{n} session{(n == 1 ? "" : "s")}";
    }

    private void StopSearchingReplies()
    {
        _replySearch?.Cancel();
        _replySearch = null;
        IsSearchingReplies = false;
        ReplySearchText = null;
        foreach (var entry in _replyMatches.Keys)
        {
            entry.MatchedReply = null;
        }
        _replyMatches.Clear();
    }

    private string[] Words() => Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The sessions the search matches, by folder, most recent first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<HistoryGroup> Groups { get; private set; } = [];

    /// <summary>
    /// <see cref="Groups"/> as one list, each folder's <see cref="HistoryHeading"/> before its sessions, so the view
    /// virtualizes it: only the rows on screen are built, however long History is.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(SearchRepliesCommand))]
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
        StopSearchingReplies();
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
                // Sessions only in the library are summarized from its copies, cached as Claude Code's own are.
                var localIds = local.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
                var libraryOnly = stored.Where(e => e.IsConflictCopy || !localIds.Contains(e.Record.SessionId)).Select(e => e.TranscriptPath).ToArray();
                IReadOnlyDictionary<string, SessionSummary?> summaries = index is null
                    ? libraryOnly.ToDictionary(p => p, HistoryIndex.ReadSummary, StringComparer.Ordinal)
                    : await index.SummarizeAsync(libraryOnly).ConfigureAwait(false);
                return Merge(local, stored, summaries, machine, now, open);
            });
        }
        catch (Exception ex)
        {
            Error = $"Couldn't read the session history: {ex.Message}";
            _all = [];
        }
        // Fill the list before saying it's loaded, so nothing sees "loaded" with an empty list.
        _searchTimer?.Dispose();
        _searchTimer = null;
        await (Filtering = FilterAsync());
        IsLoading = false;
    }

    private static List<HistoryEntry> Merge(IReadOnlyList<SessionSummary> local, IReadOnlyList<LibraryEntry> stored, IReadOnlyDictionary<string, SessionSummary?> librarySummaries,
        string machine, DateTimeOffset now, HashSet<string> open)
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
                summary.MessageCount, summary.GitBranch, summary.TranscriptPath, record, libraryEntry?.TranscriptPath, isConflict: false, label: null,
                continuedElsewhere: lastUsedElsewhere, prompts: summary.Prompts));
        }

        foreach (var libraryEntry in stored)
        {
            var record = libraryEntry.Record;
            if (!libraryEntry.IsConflictCopy && seen.Contains(record.SessionId))
            {
                continue;
            }
            // A session only in the library, from another machine (or older than Claude Code's own cleanup).
            var summary = librarySummaries.GetValueOrDefault(libraryEntry.TranscriptPath);
            var title = record.Name ?? summary?.Title ?? summary?.FirstPrompt ?? "Untitled session";
            entries.Add(Entry(record.SessionId, libraryEntry.IsConflictCopy ? $"{title} ({libraryEntry.ConflictLabel})" : title,
                summary?.FirstPrompt ?? record.FirstPrompt, record.Folder, record.Machine, record.LastUsed,
                summary?.MessageCount ?? 0, record.Project?.Branch, localTranscript: null, record, libraryEntry.TranscriptPath,
                libraryEntry.IsConflictCopy, libraryEntry.ConflictLabel, prompts: summary?.Prompts));
        }
        return entries.OrderByDescending(e => e.LastActivity).ToList();

        HistoryEntry Entry(string id, string? title, string? prompt, string? folder, string lastMachine, DateTimeOffset last, int messages, string? branch,
            string? localTranscript, SessionRecord? record, string? libraryTranscript, bool isConflict, string? label, bool continuedElsewhere = false,
            string? prompts = null)
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
                Prompts = prompts,
                Folder = folder,
                Machine = who,
                LastActivity = last,
                MessageCount = messages,
                Branch = branch,
                LocalTranscript = localTranscript,
                ContinuedElsewhere = continuedElsewhere,
                Record = record,
                LibraryTranscript = libraryTranscript,
                IsConflictCopy = isConflict,
                ConflictLabel = label,
                IsOpen = open.Contains(id),
                Details = string.Join(" · ", details),
            };
        }
    }

    /// <summary>
    /// Search by title, prompt text and folder, plus the sessions <see cref="SearchRepliesCommand"/> found; grouped by
    /// folder, most recent first. The matching runs off the UI thread; a later filtering wins over an earlier one.
    /// </summary>
    private async Task FilterAsync()
    {
        var version = ++_filterVersion;
        var words = Words();
        var all = _all;
        var replies = new Dictionary<HistoryEntry, string>(_replyMatches);
        var (groups, matched) = await Task.Run(() => Match(all, words, replies));
        if (version != _filterVersion)
        {
            return;
        }
        foreach (var entry in all)
        {
            entry.MatchedReply = matched.GetValueOrDefault(entry);
        }
        Groups = groups;
        Rows = [.. groups.SelectMany(g => g.Entries.Prepend<object>(new HistoryHeading(g.Label, g.Folder)))];
    }

    private static (IReadOnlyList<HistoryGroup> Groups, Dictionary<HistoryEntry, string> Matched) Match(
        IReadOnlyList<HistoryEntry> all, string[] words, Dictionary<HistoryEntry, string> replies)
    {
        var matched = new Dictionary<HistoryEntry, string>();
        var matches = all.Where(e =>
        {
            if (MatchesWithoutReplies(e, words))
            {
                return true;
            }
            if (replies.TryGetValue(e, out var reply))
            {
                matched[e] = reply;
                return true;
            }
            return false;
        }).ToArray();
        var groups = new List<HistoryGroup>();
        foreach (var group in matches.GroupBy(e => e.Folder is null ? "" : FolderHistory.Normalize(e.Folder), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Max(e => e.LastActivity)))
        {
            var folder = group.First().Folder;
            var label = folder is null ? "Unknown folder" : Path.GetFileName(folder.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : folder;
            groups.Add(new HistoryGroup(label, folder, group.ToArray()));
        }
        return (groups, matched);
    }

    private static bool MatchesWithoutReplies(HistoryEntry e, string[] words) => words.All(w =>
        e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
        || (e.FirstPrompt?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
        || (e.Prompts?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
        || (e.Folder?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)span.TotalDays} days ago",
    };
}
