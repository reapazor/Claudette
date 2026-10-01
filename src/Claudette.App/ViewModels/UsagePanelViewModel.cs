using System.Globalization;
using Claudette.App.Controls;
using Claudette.App.Services;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A tab's tokens in the current session window, for "which tab is burning the most".</summary>
/// <param name="Share">The tab's part of all the tabs' tokens in the window, from 0 to 1.</param>
public sealed record TabBurnRow(string Name, string Tokens, string Cost, string Turns, double Share)
{
    /// <summary>"41%", for the detailed header's busiest tabs (DESIGN.md §6).</summary>
    public string ShareText => string.Create(CultureInfo.CurrentCulture, $"{Share * 100:0}%");

    /// <summary>
    /// One row per tab, the heaviest first. <paramref name="tabName"/> names open tabs; a closed one keeps the last name
    /// the usage history has for it.
    /// </summary>
    public static IReadOnlyList<TabBurnRow> From(IReadOnlyList<TabTokenSum> sums, Func<string, string?> tabName)
    {
        var total = Math.Max(1, sums.Sum(s => s.Total));
        return sums
            .OrderByDescending(s => s.Total)
            .Select(s => new TabBurnRow(
                // Turns recorded before the history kept names have none.
                tabName(s.TabId) ?? s.Name ?? "A closed tab",
                TokenTotals.Short(s.Total),
                $"${s.CostUsd:0.00}",
                $"{s.Turns} turn{(s.Turns == 1 ? "" : "s")}",
                (double)s.Total / total))
            .ToArray();
    }
}

/// <summary>A past session window or week, with the highest usage it reached.</summary>
public sealed record PastWindowRow(string When, string Peak, double Percent);

/// <summary>
/// The Usage panel (DESIGN.md §6, "Burn trendline"): larger charts of the current session and the past week, tokens
/// per tab for the current window, and past sessions and weeks from the usage history.
/// </summary>
public sealed partial class UsagePanelViewModel : ViewModelBase
{
    private static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);

    private readonly AppServices _services;
    private readonly UsageTracker _tracker;
    private readonly UsageViewModel _header;
    private readonly Func<string, string?> _tabName;

    public UsagePanelViewModel(AppServices services, UsageTracker tracker, UsageViewModel header, Func<string, string?> tabName)
    {
        _services = services;
        _tracker = tracker;
        _header = header;
        _tabName = tabName;
        Refresh();
    }

    /// <summary>Counts refreshes, so a slow one that finishes after a later one doesn't overwrite it.</summary>
    private int _refreshes;

    public UsageViewModel Header => _header;

    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> WeekPoints { get; set; } = [];

    [ObservableProperty]
    public partial DateTimeOffset WeekStart { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset WeekEnd { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TabBurnRow> Tabs { get; set; } = [];

    public bool HasTabs => Tabs.Count > 0;

    [ObservableProperty]
    public partial IReadOnlyList<PastWindowRow> PastSessions { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<PastWindowRow> PastWeeks { get; set; } = [];

    /// <summary>How many past sessions and weeks are listed before <b>Show more</b>.</summary>
    public const int SessionsPage = 30;

    public const int WeeksPage = 12;

    private IReadOnlyList<PastWindowRow> _pastSessions = [];
    private IReadOnlyList<PastWindowRow> _pastWeeks = [];
    private int _sessionsShown = SessionsPage;
    private int _weeksShown = WeeksPage;

    public string MoreSessionsText => MoreText(_pastSessions.Count - _sessionsShown, SessionsPage);

    public bool HasMoreSessions => _pastSessions.Count > _sessionsShown;

    public string MoreWeeksText => MoreText(_pastWeeks.Count - _weeksShown, WeeksPage);

    public bool HasMoreWeeks => _pastWeeks.Count > _weeksShown;

    [RelayCommand]
    private void ShowMoreSessions()
    {
        _sessionsShown += SessionsPage;
        ShowPastWindows();
    }

    [RelayCommand]
    private void ShowMoreWeeks()
    {
        _weeksShown += WeeksPage;
        ShowPastWindows();
    }

    private void ShowPastWindows()
    {
        PastSessions = _pastSessions.Take(_sessionsShown).ToArray();
        PastWeeks = _pastWeeks.Take(_weeksShown).ToArray();
        OnPropertyChanged(nameof(MoreSessionsText));
        OnPropertyChanged(nameof(HasMoreSessions));
        OnPropertyChanged(nameof(MoreWeeksText));
        OnPropertyChanged(nameof(HasMoreWeeks));
    }

    private static string MoreText(int left, int page) => left > page ? $"Show {page} more ({left} left)" : $"Show {left} more";

    public string HistoryNote => $"Usage history is kept for {_services.Settings.Usage.KeepHistory.Label().ToLowerInvariant()} (Settings → Usage).";

    /// <summary>Reloads the panel from the usage history, in the background (after each turn and each import).</summary>
    public void Refresh() => _ = RefreshAsync();

    /// <summary>
    /// Queries the usage history off the UI thread (a group-by over all of it, for the past windows), then shows the
    /// result.
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = ++_refreshes;
        var now = _services.Time.GetUtcNow();
        var store = _tracker.Store;
        var snapshot = _tracker.Current;

        // The week: the weekly limit's own window when known, else the last 7 days.
        var weekEnd = snapshot?.WeeklyAll?.ResetsAt is { } weekReset && weekReset > now ? weekReset : now;
        var weekStart = weekEnd - TimeSpan.FromDays(7);
        // Tokens per tab since the current session window started.
        var windowStart = snapshot?.Session?.ResetsAt is { } sessionReset ? sessionReset - SessionWindow : now - SessionWindow;
        (IReadOnlyList<UsageSample> Samples, IReadOnlyList<TabTokenSum> Tabs, IReadOnlyList<WindowPeak> Sessions, IReadOnlyList<WindowPeak> Weeks) data;
        try
        {
            data = await Task.Run(() => (
                store.GetSamples(weekStart, now),
                store.GetTokensByTab(windowStart),
                store.GetPastWindows(UsageWindow.Session, now),
                store.GetPastWindows(UsageWindow.Weekly, now)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The history may be closing as Claudette quits, or damaged (it starts again by itself): show what's there.
            return;
        }
        if (generation != _refreshes)
        {
            return;
        }
        WeekEnd = weekEnd;
        WeekStart = weekStart;
        WeekPoints = data.Samples.Where(s => s.WeeklyPercent is not null).Select(s => new ChartPoint(s.Timestamp, s.WeeklyPercent!.Value)).ToArray();
        Tabs = TabBurnRow.From(data.Tabs, _tabName);
        OnPropertyChanged(nameof(HasTabs));

        // Past windows, as far back as the history goes; the lists show a page at a time.
        _pastSessions = data.Sessions
            .Select(p => Row($"Session ending {p.ResetsAt.ToLocalTime():ddd MMM d, t}", p.PeakPercent))
            .ToArray();
        _pastWeeks = data.Weeks
            .Select(p => Row($"Week ending {p.ResetsAt.ToLocalTime():ddd MMM d}", p.PeakPercent))
            .ToArray();
        ShowPastWindows();
        OnPropertyChanged(nameof(HistoryNote));

        static PastWindowRow Row(string when, double peak) => new(when, $"peaked at {peak:0}%", Math.Clamp(peak, 0, 100));
    }
}
