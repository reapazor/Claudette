using System.Collections.ObjectModel;
using Claudette.App.Controls;
using Claudette.App.Services;
using Claudette.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One limit meter in the header: session, weekly, or a model's weekly limit.</summary>
public sealed partial class MeterViewModel(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; } = "";

    [ObservableProperty]
    public partial string? ResetText { get; set; }

    [ObservableProperty]
    public partial string? Tooltip { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWarning), nameof(IsCritical))]
    public partial UsageLevel Level { get; set; }

    public bool IsWarning => Level == UsageLevel.Warning;

    public bool IsCritical => Level == UsageLevel.Critical;
}

/// <summary>
/// The usage header (DESIGN.md §6, "Header meters" and "Burn trendline"): the session meter with its countdown,
/// sparkline and projection, the weekly meters, and in-app usage alerts. Click to open the Usage panel. Its chevron
/// draws it taller with charts: see <c>UsageViewModel.Details.cs</c>.
/// </summary>
public sealed partial class UsageViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);

    private readonly AppServices _services;
    private readonly UsageTracker _tracker;
    private readonly UsageAlerts _alerts = new();
    private readonly ITimer _clock;
    private List<UsagePoint> _history = [];

    public UsageViewModel(AppServices services, UsageTracker tracker)
    {
        _services = services;
        _tracker = tracker;
        _tracker.Updated += OnUpdated;
        _tracker.TurnRecorded += OnTurnRecorded;
        _tracker.SamplesImported += OnSamplesImported;
        _services.SettingsChanged += (_, _) => Refresh();
        _services.StateChanged += OnStateChanged;
        _services.UsageHistoryCleared += OnUsageHistoryCleared;
        // The countdown and projection move on even when nothing new arrives.
        _clock = services.Time.CreateTimer(_ => services.Dispatcher.Post(Refresh), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        LoadHistory();
        Refresh();
        IsDetailed = services.State.DetailedUsageHeader;
    }

    public MeterViewModel Session { get; } = new("Session");

    /// <summary>The weekly limit across all models, then each model-specific weekly limit.</summary>
    public ObservableCollection<MeterViewModel> Weekly { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetails))]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial string? ProjectionText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectionWarning))]
    public partial bool HitsLimitBeforeReset { get; set; }

    public bool IsProjectionWarning => HitsLimitBeforeReset;

    /// <summary>Usage over the current session window, for the sparkline.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> SessionPoints { get; set; } = [];

    /// <summary>The dotted line that continues the current rate.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> SessionProjection { get; set; } = [];

    [ObservableProperty]
    public partial DateTimeOffset? WindowStart { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? WindowEnd { get; set; }

    public double WarnPercent => _services.Settings.Usage.WarnPercent;

    public double CriticalPercent => _services.Settings.Usage.CriticalPercent;

    /// <summary>"as of 14:02" when the values are old, for example after a restart before the first poll.</summary>
    [ObservableProperty]
    public partial string? AsOfText { get; set; }

    /// <summary>
    /// The latest usage alert, shown under the header until dismissed. A threshold alert's numbers follow the session
    /// meter.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlert))]
    public partial UsageAlert? Alert { get; set; }

    public bool HasAlert => Alert is not null;

    [RelayCommand]
    private void DismissAlert() => Alert = null;

    /// <summary>Set by the view: opens the Usage panel.</summary>
    public Func<Task>? ShowUsagePanel { get; set; }

    [RelayCommand]
    private Task OpenPanelAsync() => ShowUsagePanel?.Invoke() ?? Task.CompletedTask;

    private void OnUpdated(UsageSnapshot snapshot)
    {
        if (snapshot.Session is { } session)
        {
            _history.Add(new UsagePoint(snapshot.AsOf, session.Percent));
            var cutoff = snapshot.AsOf - SessionWindow;
            _history.RemoveAll(p => p.Time < cutoff);
        }
        Refresh();
        QueryDetailsIfDue(snapshot);
        var usage = _services.Settings.Usage;
        foreach (var alert in _alerts.Observe(snapshot, Projection(snapshot), usage.WarnPercent, usage.CriticalPercent))
        {
            // The line under the header, and an OS notification when Claudette isn't in front (DESIGN.md §6, §10).
            Alert = alert;
            _services.Notifications.Notify(NotificationKind.UsageAlert, alert.Title, alert.Message, key: alert.Kind.ToString());
        }
    }

    /// <summary>
    /// Another machine's readings arrived (DESIGN.md §6, "Sharing across machines"): the trendline, the projection and
    /// the charts take them in.
    /// </summary>
    private void OnSamplesImported()
    {
        LoadHistory();
        Refresh();
        if (IsDetailed)
        {
            QueryDetails();
        }
    }

    /// <summary>Samples from this session window, so the trendline survives a restart.</summary>
    private void LoadHistory()
    {
        var now = _services.Time.GetUtcNow();
        try
        {
            _history = _tracker.Store.GetSamples(now - SessionWindow, now)
                .Where(s => s.SessionPercent is not null)
                .Select(s => new UsagePoint(s.Timestamp, s.SessionPercent!.Value))
                .ToList();
        }
        catch (Exception)
        {
            _history = [];
        }
    }

    private BurnProjection? Projection(UsageSnapshot snapshot) =>
        snapshot.Session is { } session
            ? BurnRate.Project(_history, session.Percent, session.ResetsAt, _services.Time.GetUtcNow(), TimeSpan.FromMinutes(_services.Settings.Usage.BurnRateWindowMinutes))
            : null;

    private void Refresh()
    {
        OnPropertyChanged(nameof(WarnPercent));
        OnPropertyChanged(nameof(CriticalPercent));
        var snapshot = _tracker.Current;
        HasData = snapshot?.Session is not null || snapshot?.WeeklyAll is not null;
        if (snapshot is null)
        {
            return;
        }
        var now = _services.Time.GetUtcNow();
        var usage = _services.Settings.Usage;
        AsOfText = now - snapshot.AsOf > TimeSpan.FromMinutes(10) ? $"as of {snapshot.AsOf.ToLocalTime():t}" : null;

        BurnProjection? projection = null;
        if (snapshot.Session is { } session)
        {
            Fill(Session, session, now, weekly: false);
            projection = Projection(snapshot)!;
            ProjectionText = projection.Describe();
            HitsLimitBeforeReset = projection.HitsLimitBeforeReset;
            WindowEnd = session.ResetsAt;
            WindowStart = session.ResetsAt - SessionWindow;
            SessionPoints = _history.Select(p => new ChartPoint(p.Time, p.Percent)).Append(new ChartPoint(now, session.Percent)).ToArray();
            SessionProjection = ProjectionLine(projection, session.ResetsAt, now);
            if (Alert is { } alert)
            {
                // The line under the header keeps saying what the meter says.
                Alert = UsageAlerts.Refresh(alert, session, now);
            }
        }

        var weekly = new List<LimitReading>();
        if (snapshot.WeeklyAll is { } all)
        {
            weekly.Add(all);
        }
        if (usage.ShowModelMeters && _tracker.ModelLimitsAvailable)
        {
            weekly.AddRange(snapshot.WeeklyModels);
        }
        // Keep meter objects stable so the header doesn't flicker.
        var labels = weekly.Select(r => WeeklyLabel(r)).ToList();
        for (var i = Weekly.Count - 1; i >= 0; i--)
        {
            if (!labels.Contains(Weekly[i].Label))
            {
                Weekly.RemoveAt(i);
            }
        }
        foreach (var reading in weekly)
        {
            var label = WeeklyLabel(reading);
            var meter = Weekly.FirstOrDefault(m => m.Label == label);
            if (meter is null)
            {
                meter = new MeterViewModel(label);
                Weekly.Add(meter);
            }
            Fill(meter, reading, now, weekly: true);
        }

        if (IsDetailed)
        {
            RefreshDetails(snapshot, projection, now);
        }
    }

    /// <summary>
    /// The dotted line that carries the rate on from now: to where it hits the limit, or to the reset. None when
    /// there's no rate, or the reset isn't known.
    /// </summary>
    private static IReadOnlyList<ChartPoint> ProjectionLine(BurnProjection projection, DateTimeOffset? resetsAt, DateTimeOffset now) =>
        projection is { IsIdle: false, RatePerHour: > 0 } && resetsAt is { } end
            ? [new ChartPoint(now, projection.CurrentPercent), new ChartPoint(projection.LimitAt is { } limit && limit < end ? limit : end, Math.Min(100, projection.PercentAtReset ?? 100))]
            : [];

    private void Fill(MeterViewModel meter, LimitReading reading, DateTimeOffset now, bool weekly)
    {
        var usage = _services.Settings.Usage;
        meter.Percent = Math.Clamp(reading.Percent, 0, 100);
        meter.PercentText = $"{reading.Percent:0}%";
        meter.Level = BurnRate.Level(reading.Percent, usage.WarnPercent, usage.CriticalPercent);
        if (reading.ResetsAt is { } resets)
        {
            var local = resets.ToLocalTime();
            meter.ResetText = weekly ? $"resets {local:ddd h:mm tt}" : $"resets in {BurnRate.FormatCountdown(resets - now)}";
            meter.Tooltip = $"{meter.Label}: {reading.Percent:0}% used. Resets {local:dddd, MMM d} at {local:t}.";
        }
        else
        {
            meter.ResetText = null;
            meter.Tooltip = $"{meter.Label}: {reading.Percent:0}% used.";
        }
    }

    private static string WeeklyLabel(LimitReading reading) => reading.Kind == LimitKind.WeeklyAll ? "Weekly" : reading.Label;

    public void Dispose()
    {
        _tracker.Updated -= OnUpdated;
        _tracker.TurnRecorded -= OnTurnRecorded;
        _tracker.SamplesImported -= OnSamplesImported;
        _services.StateChanged -= OnStateChanged;
        _services.UsageHistoryCleared -= OnUsageHistoryCleared;
        // A read of the history still running is dropped.
        _detailsQueries++;
        _clock.Dispose();
    }
}
