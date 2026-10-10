using System.Collections.Concurrent;
using Claudette.App.Services;
using Claudette.Core;
using Claudette.Core.Git;
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

    /// <summary>
    /// The project it's listed under: its folder, or for a worktree Claude Code made, the repository's main checkout
    /// (DESIGN.md §9, "History").
    /// </summary>
    public string? ProjectFolder { get; init; }

    /// <summary><see cref="ProjectFolder"/> normalized, to group by; empty when the folder isn't known.</summary>
    public string ProjectKey { get; init; } = "";

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

    /// <summary>
    /// The session's mark as stored (DESIGN.md §4, "Marks"): the library record's or this machine's, whichever is newer.
    /// Kept as written, so opening the session keeps a mark a later Claudette added.
    /// </summary>
    public string? MarkKey { get; init; }

    public TabMark? Mark => TabMarks.Parse(MarkKey);

    public string? MarkTip => Mark is { } mark ? TabMarkText.Tip(mark) : null;

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
/// A project among History's chips (DESIGN.md §9, "History"): a folder, with the worktrees Claude Code made in it.
/// Picking one lists only its sessions.
/// </summary>
public sealed partial class HistoryProject(string key, string? folder) : ViewModelBase
{
    /// <summary>The folder normalized, as <see cref="HistoryEntry.ProjectKey"/>; empty for sessions whose folder isn't known.</summary>
    public string Key { get; } = key;

    public string? Folder { get; } = folder;

    public string Label { get; } = HistoryViewModel.ProjectLabel(folder);

    /// <summary>How many of its sessions the search matches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Its chip to a screen reader, which would otherwise read the name and a bare number: "starfall, 5 sessions".</summary>
    public string AccessibleName => HistoryViewModel.ChipName(Label, Count);
}

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

    /// <param name="project">A folder to list only the project of, as <b>History for this folder</b> does; null for every project.</param>
    public HistoryViewModel(AppServices services, ShellViewModel shell, string? project = null)
    {
        _services = services;
        _shell = shell;
        if (project is not null)
        {
            // Its chip shows straight away, and stays even if the project has no sessions yet.
            var selected = new HistoryProject(ProjectKey(project), GitWorktrees.MainCheckoutOf(project) ?? project) { IsSelected = true };
            _projects[selected.Key] = selected;
            SelectedProject = selected;
            LayOutChips();
        }
        _ = LoadAsync();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchRepliesCommand))]
    public partial string Search { get; set; } = "";

    /// <summary>How long typing pauses before History filters: a long one is thousands of sessions' prompts.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    private UiTimeout SearchWait => field ??= new(_services.Time, _services.Dispatcher);
    private int _filterVersion;
    private bool _replyFilterQueued;

    /// <summary>The latest filtering, for tests to wait on.</summary>
    internal Task Filtering { get; private set; } = Task.CompletedTask;

    /// <summary>A search is waiting for typing to pause, or being filtered.</summary>
    internal bool IsFilterPending => SearchWait.IsPending || !Filtering.IsCompleted;

    partial void OnSearchChanged(string value)
    {
        // A new search: the replies' matches were for the old one.
        StopSearchingReplies();
        SearchWait.Restart(SearchDelay, () => Filtering = FilterAsync());
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

    // ---- Projects (DESIGN.md §9, "History") --------------------------------------------------------------------

    /// <summary>How many projects have a chip of their own; the rest are under <see cref="MoreText"/>.</summary>
    public const int ChipCount = 5;

    /// <summary>Every project made so far, by key, so a chip keeps its place and focus as History updates.</summary>
    private readonly Dictionary<string, HistoryProject> _projects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every project, the most recently used first.</summary>
    private IReadOnlyList<HistoryProject> _projectOrder = [];

    /// <summary>The project listed, or null for every project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSelected))]
    public partial HistoryProject? SelectedProject { get; private set; }

    public bool IsAllSelected => SelectedProject is null;

    /// <summary>How many sessions the search matches in every project, for the All chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllAccessibleName))]
    public partial int AllCount { get; private set; }

    public string AllAccessibleName => ChipName("All projects", AllCount);

    internal static string ChipName(string label, int count) => $"{label}, {count} session{(count == 1 ? "" : "s")}";

    /// <summary>The most recently used projects, and the selected one, which takes the last place when it isn't among them.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryProject> Chips { get; private set; } = [];

    /// <summary>The projects without a chip, under <see cref="MoreText"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreProjects), nameof(MoreText))]
    public partial IReadOnlyList<HistoryProject> MoreProjects { get; private set; } = [];

    public bool HasMoreProjects => MoreProjects.Count > 0;

    public string MoreText => $"+{MoreProjects.Count} more";

    /// <summary>Lists only <paramref name="project"/>'s sessions; null, or the selected project again, lists every project's.</summary>
    [RelayCommand]
    private void SelectProject(HistoryProject? project)
    {
        if (ReferenceEquals(project, SelectedProject))
        {
            project = null;
        }
        SelectedProject?.IsSelected = false;
        SelectedProject = project;
        project?.IsSelected = true;
        LayOutChips();
        // Straight away, with any search still waiting for typing to pause.
        SearchWait.Cancel();
        Filtering = FilterAsync();
    }

    /// <summary>The projects a filtering found, in order, with how many sessions the search matches in each.</summary>
    private void ShowProjects(IReadOnlyList<(string Key, string? Folder)> found, Dictionary<string, int> counts, int total)
    {
        var order = new List<HistoryProject>(found.Count + 1);
        foreach (var (key, folder) in found)
        {
            if (!_projects.TryGetValue(key, out var project))
            {
                _projects[key] = project = new HistoryProject(key, folder);
            }
            order.Add(project);
        }
        // History opened on a project with no sessions yet: it keeps its chip, so it's clear what's listed.
        if (SelectedProject is { } selected && !order.Contains(selected))
        {
            order.Add(selected);
        }
        foreach (var project in order)
        {
            project.Count = counts.GetValueOrDefault(project.Key);
        }
        AllCount = total;
        _projectOrder = order;
        LayOutChips();
    }

    private void LayOutChips()
    {
        var chips = _projectOrder.Take(ChipCount).ToList();
        // A project picked from "+N more" takes the last chip's place while it's selected, so the selection always shows.
        if (SelectedProject is { } selected && !chips.Contains(selected))
        {
            if (chips.Count == ChipCount)
            {
                chips[^1] = selected;
            }
            else
            {
                chips.Add(selected);
            }
        }
        var more = _projectOrder.Where(p => !chips.Contains(p)).ToArray();
        // Replaced only when they change, so the chips aren't built again, losing focus, on every keystroke.
        if (!chips.SequenceEqual(Chips))
        {
            Chips = chips;
        }
        if (!more.SequenceEqual(MoreProjects))
        {
            MoreProjects = more;
        }
    }

    /// <summary>The project a folder is in: the folder itself, or for a worktree Claude Code made, its main checkout; normalized.</summary>
    internal static string ProjectKey(string? folder) =>
        folder is null ? "" : FolderHistory.Normalize(GitWorktrees.MainCheckoutOf(folder) ?? folder);

    internal static string ProjectLabel(string? folder) => folder is null ? "Unknown folder" : Formats.FolderName(folder);

    /// <summary>The sessions the search matches, by folder, most recent first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<HistoryGroup> Groups { get; private set; } = [];

    /// <summary>
    /// <see cref="Groups"/> as one list, each folder's <see cref="HistoryHeading"/> before its sessions, so the view
    /// virtualizes it: only the rows on screen are built, however long History is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstLoad))]
    public partial IReadOnlyList<object> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsFirstLoad))]
    [NotifyCanExecuteChangedFor(nameof(SearchRepliesCommand))]
    public partial bool IsLoading { get; set; } = true;

    /// <summary>Loading with nothing listed yet. A refresh leaves the list in place rather than pushing it down.</summary>
    public bool IsFirstLoad => IsLoading && Rows.Count == 0;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsEmpty => !IsLoading && Groups.Count == 0;

    /// <summary>What <see cref="IsEmpty"/> says: a project with no sessions yet says so.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = NoSessions;

    private const string NoSessions = "No sessions found.";

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
        var context = new EntryContext(library.MachineName, _services.Time.GetUtcNow(),
            _shell.AllTabs.Select(t => t.State.SessionId).OfType<string>().ToHashSet(),
            // A copy: the list is built off the UI thread, which owns the state.
            new Dictionary<string, SessionMark>(_services.State.SessionMarks));
        var sessionLibrary = library.Library;
        // With nothing listed yet, sessions show as they're read. A refresh keeps the list in place until the new one is whole.
        var reading = _reading = _all.Count == 0 ? new Reading() : null;
        IReadOnlyList<HistoryEntry> all;
        try
        {
            all = await Task.Run(async () =>
            {
                // Before the scan: the records name the sessions it finds, and say when another machine used one since.
                var stored = ListLibrary(sessionLibrary);
                var records = Records(stored);
                Action<SessionSummary>? found = reading is null
                    ? null
                    : summary => Found(reading, LocalEntry(summary, records.GetValueOrDefault(summary.SessionId), context));
                var local = index is null ? [] : await index.ScanAsync(found).ConfigureAwait(false);
                // Sessions only in the library are summarized from its copies, cached as Claude Code's own are.
                var localIds = local.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
                var libraryOnly = stored.Where(e => e.IsConflictCopy || !localIds.Contains(e.Record.SessionId)).Select(e => e.TranscriptPath).ToArray();
                IReadOnlyDictionary<string, SessionSummary?> summaries = index is null
                    ? libraryOnly.ToDictionary(p => p, HistoryIndex.ReadSummary, StringComparer.Ordinal)
                    : await index.SummarizeAsync(libraryOnly).ConfigureAwait(false);
                return Merge(local, stored, records, summaries, context);
            });
        }
        catch (Exception ex)
        {
            Error = $"Couldn't read the session history: {ex.Message}";
            all = [];
        }
        // Before the whole list, so a show still waiting can't put back part of it.
        reading?.Done = true;
        _all = all;
        // Fill the list before saying it's loaded, so nothing sees "loaded" with an empty list.
        SearchWait.Cancel();
        await (Filtering = FilterAsync());
        IsLoading = false;
    }

    private static IReadOnlyList<LibraryEntry> ListLibrary(SessionLibrary library)
    {
        try
        {
            return library.List();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // ---- Showing sessions as they're read ----------------------------------------------------------------------

    /// <summary>
    /// A load that shows sessions as the scan finds them (DESIGN.md §9, "History"): the first, which has no list to
    /// keep in place. The scan adds to <see cref="Found"/> on its thread; the UI thread takes them from there.
    /// </summary>
    private sealed class Reading
    {
        private int _showQueued;

        public ConcurrentQueue<HistoryEntry> Found { get; } = new();

        /// <summary>The load's whole list has replaced what was shown.</summary>
        public bool Done { get; set; }

        /// <summary>True for the first caller since the last <see cref="Showing"/>, which queues the show.</summary>
        public bool ClaimShow() => Interlocked.Exchange(ref _showQueued, 1) == 0;

        public void Showing() => Volatile.Write(ref _showQueued, 0);
    }

    private Reading? _reading;

    /// <summary>On the scan's thread: shows the session soon, along with any others found by then.</summary>
    private void Found(Reading reading, HistoryEntry entry)
    {
        reading.Found.Enqueue(entry);
        if (reading.ClaimShow())
        {
            _services.Dispatcher.Post(() => Show(reading));
        }
    }

    private void Show(Reading reading)
    {
        reading.Showing();
        if (!ReferenceEquals(reading, _reading) || reading.Done)
        {
            return;
        }
        var found = new List<HistoryEntry>();
        while (reading.Found.TryDequeue(out var entry))
        {
            found.Add(entry);
        }
        if (found.Count == 0)
        {
            return;
        }
        _all = [.. _all.Concat(found).OrderByDescending(e => e.LastActivity)];
        Filtering = FilterAsync();
    }

    private static List<HistoryEntry> Merge(IReadOnlyList<SessionSummary> local, IReadOnlyList<LibraryEntry> stored, Dictionary<string, LibraryEntry> records,
        IReadOnlyDictionary<string, SessionSummary?> librarySummaries, EntryContext context)
    {
        var entries = local.Select(s => LocalEntry(s, records.GetValueOrDefault(s.SessionId), context)).ToList();
        var seen = local.Select(s => s.SessionId).ToHashSet();
        foreach (var libraryEntry in stored)
        {
            if (!libraryEntry.IsConflictCopy && seen.Contains(libraryEntry.Record.SessionId))
            {
                continue;
            }
            entries.Add(LibraryOnlyEntry(libraryEntry, librarySummaries.GetValueOrDefault(libraryEntry.TranscriptPath), context));
        }
        return entries.OrderByDescending(e => e.LastActivity).ToList();
    }

    /// <summary>What a load's entries are made with.</summary>
    private sealed record EntryContext(string Machine, DateTimeOffset Now, HashSet<string> Open, Dictionary<string, SessionMark> Marks);

    /// <summary>The library's entries by session id, leaving out conflict copies.</summary>
    private static Dictionary<string, LibraryEntry> Records(IReadOnlyList<LibraryEntry> stored) =>
        stored.Where(e => !e.IsConflictCopy).GroupBy(e => e.Record.SessionId).ToDictionary(g => g.Key, g => g.First());

    /// <summary>A session in Claude Code's storage on this machine, with its library entry if it has one.</summary>
    private static HistoryEntry LocalEntry(SessionSummary summary, LibraryEntry? libraryEntry, EntryContext context)
    {
        var record = libraryEntry?.Record;
        var lastUsedElsewhere = record is not null && record.Machine != context.Machine && record.LastUsed > summary.LastActivity;
        return Entry(summary.SessionId, record?.Name ?? summary.Title, summary.FirstPrompt ?? record?.FirstPrompt, summary.Folder ?? record?.Folder,
            lastUsedElsewhere ? record!.Machine : context.Machine, lastUsedElsewhere ? record!.LastUsed : summary.LastActivity,
            summary.MessageCount, summary.GitBranch, summary.TranscriptPath, record, libraryEntry?.TranscriptPath, isConflict: false, label: null,
            context, continuedElsewhere: lastUsedElsewhere, prompts: summary.Prompts);
    }

    /// <summary>A session only in the library, from another machine (or older than Claude Code's own cleanup).</summary>
    private static HistoryEntry LibraryOnlyEntry(LibraryEntry libraryEntry, SessionSummary? summary, EntryContext context)
    {
        var record = libraryEntry.Record;
        var title = record.Name ?? summary?.Title ?? summary?.FirstPrompt ?? "Untitled session";
        return Entry(record.SessionId, libraryEntry.IsConflictCopy ? $"{title} ({libraryEntry.ConflictLabel})" : title,
            summary?.FirstPrompt ?? record.FirstPrompt, record.Folder, record.Machine, record.LastUsed,
            summary?.MessageCount ?? 0, record.Project?.Branch, localTranscript: null, record, libraryEntry.TranscriptPath,
            libraryEntry.IsConflictCopy, libraryEntry.ConflictLabel, context, prompts: summary?.Prompts);
    }

    private static HistoryEntry Entry(string id, string? title, string? prompt, string? folder, string lastMachine, DateTimeOffset last, int messages, string? branch,
        string? localTranscript, SessionRecord? record, string? libraryTranscript, bool isConflict, string? label, EntryContext context,
        bool continuedElsewhere = false, string? prompts = null)
    {
        var who = lastMachine == context.Machine ? "This machine" : lastMachine;
        var details = new List<string> { who, Formats.Ago(context.Now - last) };
        if (messages > 0)
        {
            details.Add($"{messages} message{(messages == 1 ? "" : "s")}");
        }
        // A worktree Claude Code made is listed under its repository, so it says which; its own branch goes without saying.
        var worktree = folder is not null && GitWorktrees.MainCheckoutOf(folder) is not null ? Formats.FolderName(folder) : null;
        if (branch is not null && (worktree is null || branch != GitWorktrees.BranchPrefix + worktree))
        {
            details.Add(branch);
        }
        if (worktree is not null)
        {
            details.Add($"worktree {worktree}");
        }
        if (context.Open.Contains(id))
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
            ProjectFolder = folder is null ? null : GitWorktrees.MainCheckoutOf(folder) ?? folder,
            ProjectKey = ProjectKey(folder),
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
            IsOpen = context.Open.Contains(id),
            MarkKey = TabMarks.ForSession(record, context.Marks.GetValueOrDefault(id)),
            Details = string.Join(" · ", details),
        };
    }

    /// <summary>
    /// Search by title, prompt text and folder, plus the sessions <see cref="SearchRepliesCommand"/> found, in the
    /// selected project; grouped by project, most recent first. The matching runs off the UI thread; a later filtering
    /// wins over an earlier one.
    /// </summary>
    private async Task FilterAsync()
    {
        var version = ++_filterVersion;
        var words = Words();
        var all = _all;
        var replies = new Dictionary<HistoryEntry, string>(_replyMatches);
        var project = SelectedProject;
        var filtered = await Task.Run(() => Match(all, words, replies, project?.Key));
        if (version != _filterVersion)
        {
            return;
        }
        foreach (var entry in all)
        {
            entry.MatchedReply = filtered.Matched.GetValueOrDefault(entry);
        }
        ShowProjects(filtered.Projects, filtered.Counts, filtered.Total);
        EmptyText = project is not null && words.Length == 0 ? $"No sessions in {project.Label} yet." : NoSessions;
        Groups = filtered.Groups;
        Rows = [.. filtered.Groups.SelectMany(g => g.Entries.Prepend<object>(new HistoryHeading(g.Label, g.Folder)))];
    }

    /// <summary>What a filtering found.</summary>
    /// <param name="Projects">Every project, matched or not, the most recently used first.</param>
    /// <param name="Counts">The sessions the search matches, by project.</param>
    /// <param name="Total">The sessions the search matches in every project.</param>
    private sealed record Filtered(IReadOnlyList<HistoryGroup> Groups, Dictionary<HistoryEntry, string> Matched,
        IReadOnlyList<(string Key, string? Folder)> Projects, Dictionary<string, int> Counts, int Total);

    private static Filtered Match(IReadOnlyList<HistoryEntry> all, string[] words, Dictionary<HistoryEntry, string> replies, string? project)
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
        var counts = matches.CountBy(e => e.ProjectKey, StringComparer.OrdinalIgnoreCase).ToDictionary(StringComparer.OrdinalIgnoreCase);
        // In the order of each project's latest session, since the list is newest first: the chips hold still as the search changes.
        var projects = all.DistinctBy(e => e.ProjectKey, StringComparer.OrdinalIgnoreCase).Select(e => (e.ProjectKey, e.ProjectFolder)).ToArray();
        var shown = project is null ? matches : matches.Where(e => string.Equals(e.ProjectKey, project, StringComparison.OrdinalIgnoreCase));
        var groups = new List<HistoryGroup>();
        foreach (var group in shown.GroupBy(e => e.ProjectKey, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Max(e => e.LastActivity)))
        {
            var folder = group.First().ProjectFolder;
            groups.Add(new HistoryGroup(ProjectLabel(folder), folder, group.ToArray()));
        }
        return new Filtered(groups, matched, projects, counts, matches.Length);
    }

    private static bool MatchesWithoutReplies(HistoryEntry e, string[] words) => words.All(w =>
        e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
        || (e.FirstPrompt?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
        || (e.Prompts?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
        || (e.Folder?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));
}
