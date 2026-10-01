using System.Globalization;
using Claudette.App.Controls;
using Claudette.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A line of the weekly chart's legend: the limit across all models, or a model's own.</summary>
/// <param name="Series">0 for the limit across all models (the accent line), 1 and 2 for the model lines.</param>
public sealed record ChartLegendItem(string Label, string PercentText, int Series)
{
    public bool IsFirstModel => Series == 1;

    public bool IsSecondModel => Series == 2;
}

/// <summary>The week's stored samples, already cut down to one point per bucket, for the weekly chart.</summary>
/// <param name="Models">Each model-specific weekly limit's points, by its label.</param>
internal sealed record StoredWeek(IReadOnlyList<ChartPoint> Weekly, IReadOnlyDictionary<string, IReadOnlyList<ChartPoint>> Models)
{
    public static readonly StoredWeek Empty = new([], new Dictionary<string, IReadOnlyList<ChartPoint>>());

    public static StoredWeek From(IReadOnlyList<UsageSample> samples, TimeSpan bucket) => new(
        UsageViewModel.Downsample(samples.Where(s => s.WeeklyPercent is not null).Select(s => new ChartPoint(s.Timestamp, s.WeeklyPercent!.Value)), bucket),
        samples
            .SelectMany(s => s.Models.Select(m => (m.Label, Point: new ChartPoint(s.Timestamp, m.Percent))))
            .GroupBy(m => m.Label)
            .ToDictionary(g => g.Key, g => UsageViewModel.Downsample(g.Select(m => m.Point), bucket)));
}

/// <summary>
/// The detailed usage header (DESIGN.md §6, "Detailed header"): the header drawn taller with a chart of the session,
/// a chart of the week, the burn rate, the time to the limit and the busiest tabs. Its chevron and Settings →
/// Appearance turn it on and off; the choice is this machine's state. The week and the tabs come from the usage
/// history, read off the UI thread when new samples or turns arrive, and only while it's shown.
/// </summary>
public sealed partial class UsageViewModel
{
    /// <summary>Below this width the busiest tabs are left out.</summary>
    public const double DetailsTabsMinWidth = 960;

    /// <summary>Below this width the weekly chart is left out too, and the session chart takes its room.</summary>
    public const double DetailsWeekMinWidth = 700;

    public const int BusiestTabCount = 3;

    /// <summary>Model-specific weekly limits drawn on the weekly chart; two hues are all that stay apart beside the accent.</summary>
    public const int ModelLineCount = 2;

    /// <summary>The history saves a sample at most once a minute, so it isn't read more often than that.</summary>
    private static readonly TimeSpan DetailsQueryInterval = UsageStore.MinimumSampleInterval;

    /// <summary>A chart draws the last value in each bucket of this long, rather than every sample.</summary>
    internal static readonly TimeSpan SessionBucket = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan WeekBucket = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan WeekLength = TimeSpan.FromDays(7);

    private StoredWeek _storedWeek = StoredWeek.Empty;
    private IReadOnlyList<TabTokenSum> _tabSums = [];
    private DateTimeOffset? _lastDetailsQuery;
    /// <summary>The session's and the week's reset times when the history was last read, to notice a window resetting.</summary>
    private (DateTimeOffset? Session, DateTimeOffset? Week)? _queriedResets;
    private int _detailsQueries;

    /// <summary>The header is drawn taller, with charts. Remembered on this machine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetails), nameof(DetailsToggleText))]
    public partial bool IsDetailed { get; set; }

    /// <summary>Accounts without plan limits get no charts, as they get no meters.</summary>
    public bool ShowDetails => IsDetailed && HasData;

    /// <summary>The chevron's name and tooltip.</summary>
    public string DetailsToggleText => IsDetailed ? "Collapse the usage header" : "Expand the usage header";

    [RelayCommand]
    private void ToggleDetails()
    {
        IsDetailed = !IsDetailed;
        _services.State.DetailedUsageHeader = IsDetailed;
        _services.SaveState();
    }

    partial void OnIsDetailedChanged(bool value)
    {
        if (value)
        {
            Refresh();
            QueryDetails();
        }
    }

    /// <summary>Settings → Appearance changed it, or <b>Reset to defaults</b> did.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (IsDetailed != _services.State.DetailedUsageHeader)
        {
            IsDetailed = _services.State.DetailedUsageHeader;
        }
    }

    /// <summary>Opens open tabs' names for the busiest tabs, by tab id. Set by the main window.</summary>
    public Func<string, string?>? TabName { get; set; }

    // ---- The session chart --------------------------------------------------------------------------------------

    /// <summary>Usage over the current session window, from its start to now.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> DetailSessionPoints { get; set; } = [];

    /// <summary>The charts' "now" marker.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? Now { get; set; }

    /// <summary>Where the projection crosses the critical threshold, or the limit, before the reset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrossing))]
    public partial ChartPoint? SessionCrossing { get; set; }

    public bool HasCrossing => SessionCrossing is not null;

    /// <summary>"Hits 90% at 14:05, 25m before it resets."</summary>
    [ObservableProperty]
    public partial string? CrossingText { get; set; }

    // ---- The weekly chart -----------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> WeekPoints { get; set; } = [];

    /// <summary>The week's average pace so far, carried on to its reset.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> WeekProjection { get; set; } = [];

    /// <summary>Model-specific weekly limits, as thinner lines.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries> WeekModelSeries { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWeekLegend))]
    public partial IReadOnlyList<ChartLegendItem> WeekLegend { get; set; } = [];

    /// <summary>One line needs no legend: the chart's title names it.</summary>
    public bool HasWeekLegend => WeekLegend.Count > 0;

    [ObservableProperty]
    public partial DateTimeOffset? WeekStart { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? WeekEnd { get; set; }

    // ---- The numbers ------------------------------------------------------------------------------------------------

    /// <summary>"12.4%/h", "Idle", or a dash without enough data.</summary>
    [ObservableProperty]
    public partial string BurnRateText { get; set; } = "—";

    [ObservableProperty]
    public partial string BurnRateNote { get; set; } = "";

    /// <summary>"1h 05m" at this rate, or a dash.</summary>
    [ObservableProperty]
    public partial string TimeToLimitText { get; set; } = "—";

    [ObservableProperty]
    public partial string TimeToLimitNote { get; set; } = "";

    /// <summary>The top tabs by tokens since the session window started.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBusiestTabs))]
    public partial IReadOnlyList<TabBurnRow> BusiestTabs { get; set; } = [];

    public bool HasBusiestTabs => BusiestTabs.Count > 0;

    // ---- Fitting the window -----------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool ShowBusiestTabs { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionChartSpan))]
    public partial bool ShowWeekChart { get; set; } = true;

    /// <summary>The session chart takes the weekly chart's column when that's left out.</summary>
    public int SessionChartSpan => ShowWeekChart ? 1 : 2;

    /// <summary>Called by the window as it resizes: a narrow header leaves out the busiest tabs, then the weekly chart.</summary>
    public void SetDetailsWidth(double width)
    {
        ShowBusiestTabs = width >= DetailsTabsMinWidth;
        ShowWeekChart = width >= DetailsWeekMinWidth;
    }

    // ---- Filling it in ----------------------------------------------------------------------------------------------

    private void RefreshDetails(UsageSnapshot snapshot, BurnProjection? projection, DateTimeOffset now)
    {
        var usage = _services.Settings.Usage;
        Now = now;

        if (snapshot.Session is { } session && projection is not null)
        {
            var start = WindowStart ?? now - SessionWindow;
            DetailSessionPoints = Downsample(SessionPoints.Where(p => p.Time >= start && p.Time <= now), SessionBucket);
            var crossing = BurnRate.FirstCrossing(projection, session.ResetsAt, now, usage.CriticalPercent);
            SessionCrossing = crossing is null ? null : new ChartPoint(crossing.At, crossing.Level);
            CrossingText = crossing?.Describe();
            (BurnRateText, BurnRateNote) = DescribeRate(projection, usage.BurnRateWindowMinutes);
            (TimeToLimitText, TimeToLimitNote) = DescribeTimeToLimit(projection);
        }
        else
        {
            DetailSessionPoints = [];
            SessionCrossing = null;
            CrossingText = null;
            (BurnRateText, BurnRateNote) = ("—", "No session limit");
            (TimeToLimitText, TimeToLimitNote) = ("—", "");
        }

        var (weekStart, weekEnd) = WeekRange(snapshot, now);
        WeekStart = weekStart;
        WeekEnd = weekEnd;
        // The stored week, bucketed off the UI thread, and the latest reading.
        IReadOnlyList<ChartPoint> ThisWeek(IReadOnlyList<ChartPoint> stored, double current) =>
            Downsample(stored.Where(p => p.Time >= weekStart && p.Time <= now).Append(new ChartPoint(now, current)), WeekBucket);
        if (snapshot.WeeklyAll is { } weekly)
        {
            WeekPoints = ThisWeek(_storedWeek.Weekly, weekly.Percent);
            var reset = weekly.ResetsAt is { } r && r > now ? r : (DateTimeOffset?)null;
            WeekProjection = ProjectionLine(BurnRate.ProjectAverage(weekly.Percent, weekStart, reset, now), reset, now);
        }
        else
        {
            WeekPoints = [];
            WeekProjection = [];
        }

        // The same model limits as the header's meters.
        var models = usage.ShowModelMeters && _tracker.ModelLimitsAvailable ? snapshot.WeeklyModels.Take(ModelLineCount).ToArray() : [];
        WeekModelSeries = [.. models.Select(model => new ChartSeries(model.Label, ThisWeek(_storedWeek.Models.GetValueOrDefault(model.Label) ?? [], model.Percent)))];
        WeekLegend = models.Length == 0 || snapshot.WeeklyAll is not { } all
            ? []
            : [new ChartLegendItem("All models", Percent(all.Percent), 0), .. models.Select((m, i) => new ChartLegendItem(m.Label, Percent(m.Percent), i + 1))];

        BusiestTabs = [.. TabBurnRow.From(_tabSums, TabName ?? (_ => null)).Take(BusiestTabCount)];
    }

    private static string Percent(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0}%");

    private static (string Text, string Note) DescribeRate(BurnProjection projection, int windowMinutes) => projection switch
    {
        { RatePerHour: null } => ("—", "Not enough data yet"),
        { IsIdle: true } => ("Idle", $"Nothing used in the last {windowMinutes} min"),
        { RatePerHour: { } rate } => (string.Create(CultureInfo.CurrentCulture, $"{rate:0.0}%/h"), $"Over the last {windowMinutes} min"),
    };

    private static (string Text, string Note) DescribeTimeToLimit(BurnProjection projection) => projection switch
    {
        { CurrentPercent: >= 100 } => ("Now", "The limit is reached"),
        { RatePerHour: null } => ("—", "Not enough data yet"),
        { IsIdle: true } => ("—", "No recent usage"),
        { TimeToLimit: { } time, HitsLimitBeforeReset: true } => (BurnRate.FormatDuration(time), "Before it resets"),
        { TimeToLimit: { } time } => (BurnRate.FormatDuration(time), "It resets first"),
        _ => ("—", ""),
    };

    /// <summary>The week's window: up to the weekly limit's reset when known, else the last 7 days (as in the Usage panel).</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) WeekRange(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var end = snapshot?.WeeklyAll?.ResetsAt is { } reset && reset > now ? reset : now;
        return (end - WeekLength, end);
    }

    private static DateTimeOffset SessionStart(UsageSnapshot? snapshot, DateTimeOffset now) =>
        snapshot?.Session?.ResetsAt is { } reset ? reset - SessionWindow : now - SessionWindow;

    /// <summary>
    /// The last point in each <paramref name="bucket"/> of time, in time order, so a chart draws a few hundred points
    /// rather than every sample. The newest point is always kept.
    /// </summary>
    internal static IReadOnlyList<ChartPoint> Downsample(IEnumerable<ChartPoint> points, TimeSpan bucket)
    {
        var kept = new List<ChartPoint>();
        long? current = null;
        foreach (var point in points.OrderBy(p => p.Time))
        {
            var key = point.Time.UtcTicks / bucket.Ticks;
            if (key == current)
            {
                kept[^1] = point;
            }
            else
            {
                kept.Add(point);
                current = key;
            }
        }
        return kept;
    }

    // ---- Reading the history ----------------------------------------------------------------------------------------

    /// <summary>New usage arrived: read the history again if a minute has passed or a window reset.</summary>
    private void QueryDetailsIfDue(UsageSnapshot snapshot)
    {
        if (!IsDetailed)
        {
            return;
        }
        var now = _services.Time.GetUtcNow();
        if (_lastDetailsQuery is not { } last || now - last >= DetailsQueryInterval || _queriedResets != Resets(snapshot))
        {
            QueryDetails();
        }
    }

    private static (DateTimeOffset? Session, DateTimeOffset? Week) Resets(UsageSnapshot? snapshot) =>
        (snapshot?.Session?.ResetsAt, snapshot?.WeeklyAll?.ResetsAt);

    private void OnTurnRecorded(string tabId)
    {
        if (IsDetailed)
        {
            QueryDetails();
        }
    }

    private void OnUsageHistoryCleared(object? sender, bool alsoResetTabTotals)
    {
        _storedWeek = StoredWeek.Empty;
        _tabSums = [];
        if (IsDetailed)
        {
            Refresh();
            QueryDetails();
        }
    }

    /// <summary>
    /// Reads the week's samples and the tabs' tokens off the UI thread, and buckets the samples there too, then shows
    /// them. A newer read replaces an older one still running.
    /// </summary>
    private void QueryDetails()
    {
        var snapshot = _tracker.Current;
        var now = _services.Time.GetUtcNow();
        var sessionStart = SessionStart(snapshot, now);
        var weekStart = WeekRange(snapshot, now).Start;
        var query = ++_detailsQueries;
        _lastDetailsQuery = now;
        _queriedResets = Resets(snapshot);
        var store = _tracker.Store;
        _ = Task.Run(() =>
        {
            StoredWeek week;
            IReadOnlyList<TabTokenSum> tabs;
            try
            {
                week = StoredWeek.From(store.GetSamples(weekStart, now), WeekBucket);
                tabs = store.GetTokensByTab(sessionStart);
            }
            catch (Exception)
            {
                // The history is closing or unreadable: the charts keep what they show, as the header does.
                return;
            }
            _services.Dispatcher.Post(() =>
            {
                if (query == _detailsQueries)
                {
                    _storedWeek = week;
                    _tabSums = tabs;
                    Refresh();
                }
            });
        });
    }
}
