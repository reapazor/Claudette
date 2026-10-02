using Claudette.Core.Files;
using Claudette.Core.Git;
using Claudette.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// Removes worktree tabs' worktrees on their own, as Settings → General says (DESIGN.md §4, "Cleaning up worktrees"):
/// those with nothing of their own once a day has passed, and those no tab has used for the days chosen. A minute after
/// launch, then every hour, through the injected clock. Git runs off the UI thread; the tabs and the state are read and
/// changed on it. Git lists a worktree by its real path, and a tab names it as it was opened (on macOS, under
/// <c>/var</c> rather than <c>/private/var</c>), so paths are matched with their links followed (<see cref="RealPath"/>).
/// </summary>
public sealed class WorktreeCleanupService : IDisposable
{
    /// <summary>The first pass waits this long after launch, so it doesn't slow the start.</summary>
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly AppServices _services;
    private readonly ILogger _logger;
    private ITimer? _timer;
    private int _running;

    public WorktreeCleanupService(AppServices services)
    {
        _services = services;
        _logger = services.Loggers.CreateLogger("Worktrees");
    }

    /// <summary>The open tabs' states, read on the UI thread: their worktrees are in use. The shell sets it.</summary>
    public Func<IReadOnlyList<TabState>> OpenTabs { get; set; } = () => [];

    private AppState State => _services.State;

    private WorktreeCleanupRules Rules => new(_services.Settings.General.RemoveMergedWorktrees, _services.Settings.General.RemoveInactiveWorktreesAfterDays);

    /// <summary>Starts the passes: the first a minute from now, then every hour. Each does nothing while the rules are off.</summary>
    public void Start() =>
        _timer ??= _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() => _ = RunAsync()), null, FirstRunDelay, Interval);

    /// <summary>A tab stopped working in its worktree: inactivity counts from now.</summary>
    public void MarkUsed(TabState tab)
    {
        if (WorksInWorktree(tab))
        {
            SetLastUsed(tab.Folder, _services.Time.GetUtcNow());
            _services.SaveState();
        }
    }

    /// <summary>The tab works in a worktree Claude Code made: a worktree tab's, or one opened there by folder or from History.</summary>
    private static bool WorksInWorktree(TabState tab) => tab.NewWorktree is null && GitWorktrees.MainCheckoutOf(tab.Folder) is not null;

    /// <summary>
    /// One pass, on the UI thread: removes what the rules say and returns the removed worktrees' paths. Nothing while the
    /// rules are off, or while a pass is under way.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync()
    {
        var rules = Rules;
        if (!rules.IsOn || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return [];
        }
        var removed = new List<string>();
        try
        {
            var now = _services.Time.GetUtcNow();
            var tabs = OpenTabs();
            // The worktrees open tabs work in are in use now.
            foreach (var tab in tabs.Where(WorksInWorktree))
            {
                SetLastUsed(tab.Folder, now);
            }
            var checkouts = tabs.Select(t => t.WorktreeOf ?? GitWorktrees.MainCheckoutOf(t.Folder) ?? t.Folder)
                .Concat(State.RecentFolders.Select(r => r.Path))
                .Concat(State.FavoriteFolders)
                .Concat(State.WorktreesLastUsed.Keys.Select(GitWorktrees.MainCheckoutOf).OfType<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var git = new GitWorktrees(_services.Git);
            var roots = new List<string>();
            foreach (var checkout in checkouts)
            {
                if (!Directory.Exists(checkout) || await _services.Git.GetRepositoryRootAsync(checkout).ConfigureAwait(true) is not { } root
                    || roots.Any(r => FolderHistory.SamePath(r, root)))
                {
                    continue;
                }
                roots.Add(root);
                if (await git.ListAsync(root).ConfigureAwait(true) is not { } worktrees)
                {
                    continue;
                }
                foreach (var worktree in worktrees.Where(w => !w.IsMain && GitWorktrees.MainCheckoutOf(w.Path) is not null && Directory.Exists(w.Path)))
                {
                    if (InUse(OpenTabs(), root, worktree.Path))
                    {
                        continue;
                    }
                    var lastUsed = LastUsed(worktree.Path) ?? SetLastUsed(worktree.Path, now);
                    var work = await git.InspectAsync(worktree).ConfigureAwait(true);
                    var verdict = WorktreeCleanup.Decide(worktree, work, lastUsed, rules, now);
                    // A tab may have opened in it while git looked.
                    if (verdict == WorktreeVerdict.Keep || work is null || InUse(OpenTabs(), root, worktree.Path))
                    {
                        continue;
                    }
                    if (await git.RemoveAsync(root, worktree, discard: false, work.OwnCommits).ConfigureAwait(true) is { } error)
                    {
                        _logger.LogWarning("Couldn't remove the worktree {Path}: {Error}", worktree.Path, error);
                        continue;
                    }
                    _logger.LogInformation("Removed the worktree {Path} ({Verdict}).", worktree.Path, verdict);
                    removed.Add(worktree.Path);
                    RemoveLastUsed(worktree.Path);
                }
            }
            // Forget worktrees that are gone, however they went.
            foreach (var gone in State.WorktreesLastUsed.Keys.Where(p => !Directory.Exists(p)).ToList())
            {
                State.WorktreesLastUsed.Remove(gone);
            }
            if (removed.Count > 0)
            {
                State.LastWorktreeCleanup = new WorktreeCleanupRecord { At = now, Removed = removed };
            }
            _services.SaveState();
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
        return removed;
    }

    /// <summary>A tab works in <paramref name="path"/>, or will: one waiting to make it with <c>--worktree</c>.</summary>
    private static bool InUse(IEnumerable<TabState> tabs, string root, string path) => tabs.Any(t =>
        RealPath.Same(t.Folder, path)
        || t.NewWorktree is { } pending && RealPath.Same(GitWorktrees.PathFor(root, pending), path));

    /// <summary>When a tab last used the worktree: the latest time kept under any spelling of its path.</summary>
    private DateTimeOffset? LastUsed(string path)
    {
        DateTimeOffset? latest = null;
        foreach (var (key, when) in State.WorktreesLastUsed)
        {
            if (RealPath.Same(key, path) && (latest is null || when > latest))
            {
                latest = when;
            }
        }
        return latest;
    }

    private DateTimeOffset SetLastUsed(string path, DateTimeOffset when)
    {
        RemoveLastUsed(path);
        State.WorktreesLastUsed[FolderHistory.Normalize(path)] = when;
        return when;
    }

    private void RemoveLastUsed(string path)
    {
        foreach (var key in State.WorktreesLastUsed.Keys.Where(k => RealPath.Same(k, path)).ToList())
        {
            State.WorktreesLastUsed.Remove(key);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
