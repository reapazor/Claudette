using Claudette.App.Controls;
using Claudette.App.Services;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A tab's tokens in the current session window, for "which tab is burning the most".</summary>
public sealed record TabBurnRow(string Name, string Tokens, string Cost, string Turns, double Share);

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

    public void Refresh()
    {
        var now = _services.Time.GetUtcNow();
        var store = _tracker.Store;
        var snapshot = _tracker.Current;

        // The week: the weekly limit's own window when known, else the last 7 days.
        WeekEnd = snapshot?.WeeklyAll?.ResetsAt is { } weekReset && weekReset > now ? weekReset : now;
        WeekStart = WeekEnd - TimeSpan.FromDays(7);
        var samples = store.GetSamples(WeekStart, now);
        WeekPoints = samples.Where(s => s.WeeklyPercent is not null).Select(s => new ChartPoint(s.Timestamp, s.WeeklyPercent!.Value)).ToArray();

        // Tokens per tab since the current session window started.
        var windowStart = snapshot?.Session?.ResetsAt is { } sessionReset ? sessionReset - SessionWindow : now - SessionWindow;
        var sums = store.GetTokensByTab(windowStart);
        var total = Math.Max(1, sums.Sum(s => s.Input + s.Output + s.CacheWrite + s.CacheRead));
        Tabs = sums
            .Select(s => (Sum: s, Tokens: s.Input + s.Output + s.CacheWrite + s.CacheRead))
            .OrderByDescending(s => s.Tokens)
            .Select(s => new TabBurnRow(
                _tabName(s.Sum.TabId) ?? "A closed tab",
                TokenTotals.Short(s.Tokens),
                $"${s.Sum.CostUsd:0.00}",
                $"{s.Sum.Turns} turn{(s.Sum.Turns == 1 ? "" : "s")}",
                (double)s.Tokens / total))
            .ToArray();
        OnPropertyChanged(nameof(HasTabs));

        // Past windows, as far back as the history goes; the lists show a page at a time.
        _pastSessions = store.GetPastWindows(UsageWindow.Session, now)
            .Select(p => Row($"Session ending {p.ResetsAt.ToLocalTime():ddd MMM d, t}", p.PeakPercent))
            .ToArray();
        _pastWeeks = store.GetPastWindows(UsageWindow.Weekly, now)
            .Select(p => Row($"Week ending {p.ResetsAt.ToLocalTime():ddd MMM d}", p.PeakPercent))
            .ToArray();
        ShowPastWindows();
        OnPropertyChanged(nameof(HistoryNote));

        static PastWindowRow Row(string when, double peak) => new(when, $"peaked at {peak:0}%", Math.Clamp(peak, 0, 100));
    }
}
