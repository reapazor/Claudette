using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>The ring on a tab's row (DESIGN.md §4, "Sidebar"): how full its context window is.</summary>
public enum ContextLevel
{
    /// <summary>No context data yet: the ring is hidden.</summary>
    None,
    Normal,
    /// <summary>Near the point where Claude Code compacts by itself: amber.</summary>
    High,
    /// <summary>Nearly full: red.</summary>
    Critical,
}

/// <summary>A model's row in the tokens flyout (DESIGN.md §4, "Token stats per tab").</summary>
public sealed record TokenRow(string Model, string Input, string Output, string CacheWrite, string CacheRead, string Cost);

/// <summary>
/// What the composer bar's context indicator and the ring on the tab's row say (DESIGN.md §6, "Per-tab context"), from
/// <c>get_context_usage</c> or, without it, estimated from the main agent's latest call.
/// </summary>
/// <param name="Text">"Context 42%".</param>
/// <param name="Detail">"84,000 of 200,000 tokens · auto-compacts at 160,000".</param>
/// <param name="IsHigh">Near the point where Claude Code compacts by itself, or past 80% without auto-compact.</param>
public sealed record ContextIndicator(string Text, string Detail, bool IsHigh, double Percent)
{
    /// <summary>How close to the auto-compact point counts as near it.</summary>
    public const double NearAutoCompact = 0.9;

    /// <summary>Without auto-compact, how full counts as high.</summary>
    public const double HighPercent = 80;

    public static ContextIndicator From(long tokens, long window, double percent, long? autoCompactAt, bool estimated)
    {
        var compacts = autoCompactAt is { } threshold ? $" · auto-compacts at {threshold:N0}" : "";
        var detail = estimated
            ? $"about {tokens:N0} of {window:N0} tokens, estimated from the last call{compacts}"
            : $"{tokens:N0} of {window:N0} tokens{compacts}";
        var high = autoCompactAt is { } limit ? tokens >= limit * NearAutoCompact : percent >= HighPercent;
        return new ContextIndicator($"Context {percent:0}%", detail, high, percent);
    }
}

/// <summary>What the context indicator and the token counts need from their tab.</summary>
internal interface IContextHost : ITabAreaHost
{
    /// <summary>The name to show for a model id.</summary>
    string? ModelDisplayName(string? id);

    /// <summary>The turn's tokens moved on mid-turn: the working line shows them.</summary>
    void TurnTokensChanged();
}

/// <summary>
/// A tab's context and tokens (DESIGN.md §4, "Token stats per tab"; §6, "Per-tab context"): the composer bar's context
/// indicator and token count, the ring on the tab's row, what fills the context window, and the tokens flyout.
/// </summary>
public sealed partial class ContextViewModel : ObservableObject
{
    /// <summary>From this full, the ring on the tab's row turns red (DESIGN.md §4, "Sidebar").</summary>
    public const double CriticalPercent = 95;

    private readonly AppServices _services;
    private readonly IContextHost _host;

    /// <summary>Per-call usage from assistant messages, for live counts mid-turn and the estimate (DESIGN.md §6).</summary>
    private readonly CallUsage _callUsage = new();

    /// <summary><c>get_context_usage</c> failed for this session, so the indicator is estimated from each call.</summary>
    private bool _usageUnavailable;

    /// <summary>When Claude Code compacts by itself, from <c>autocompact_state</c>, for the estimate's warning.</summary>
    private AutocompactStateMessage? _autocompact;

    internal ContextViewModel(AppServices services, IContextHost host)
    {
        _services = services;
        _host = host;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial string? Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial string? Detail { get; set; }

    /// <summary>Near the point where Claude Code compacts by itself: the indicator and the ring turn amber.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Level))]
    public partial bool IsHigh { get; set; }

    /// <summary>How full the context window is, 0–100. Null until the tab has context data (it hasn't started yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Level), nameof(IsCritical), nameof(Sweep), nameof(ShowRing))]
    public partial double? Percent { get; set; }

    /// <summary>What the ring on the tab's row shows: nothing, muted, amber or red.</summary>
    public ContextLevel Level => Percent is not { } percent ? ContextLevel.None
        : percent >= CriticalPercent ? ContextLevel.Critical
        : IsHigh ? ContextLevel.High
        : ContextLevel.Normal;

    public bool IsCritical => Level == ContextLevel.Critical;

    /// <summary>The ring's arc, in degrees clockwise from the top.</summary>
    public double Sweep => Math.Clamp(Percent ?? 0, 0, 100) * 3.6;

    /// <summary>The ring on the tab's row: once there's context data, unless Settings → Appearance turns it off.</summary>
    public bool ShowRing => Percent is not null && _services.Settings.Appearance.ShowContextOnTabs;

    /// <summary>The ring's tooltip: the composer bar's context text and its detail.</summary>
    public string? Tip => Text is not { } text ? null : Detail is { } detail ? $"{text} ({detail})" : text;

    /// <summary>
    /// What fills the context window, for the flyout the context ring and the composer's indicator open (DESIGN.md §6,
    /// "Per-tab context"). From the same <c>get_context_usage</c> reply as the indicator, or the estimate without it.
    /// </summary>
    [ObservableProperty]
    public partial ContextBreakdown? Breakdown { get; set; }

    partial void OnDetailChanged(string? value) => _host.InfoRowsChanged();

    /// <summary>Settings → Appearance changed: the ring may have been turned on or off.</summary>
    internal void OnSettingsChanged() => OnPropertyChanged(nameof(ShowRing));

    private void Show(ContextIndicator indicator, ContextBreakdown breakdown)
    {
        Text = indicator.Text;
        Detail = indicator.Detail;
        IsHigh = indicator.IsHigh;
        Percent = indicator.Percent;
        Breakdown = breakdown.KeepingExpanded(Breakdown);
    }

    /// <summary>
    /// Asks Claude Code what fills the context window (<c>get_context_usage</c>), as the session starts and after each
    /// turn. Without it, the indicator is estimated from each call.
    /// </summary>
    internal async Task RefreshUsageAsync(ClaudeSession session)
    {
        ContextUsage usage;
        try
        {
            usage = await session.GetContextUsageAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not offered by this Claude Code, or it failed: estimate from the last call instead.
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _host.Session))
                {
                    _usageUnavailable = true;
                    ShowEstimate();
                }
            });
            return;
        }
        _services.Dispatcher.Post(() =>
        {
            if (!ReferenceEquals(session, _host.Session))
            {
                return;
            }
            _usageUnavailable = false;
            Show(ContextIndicator.From(usage.TotalTokens, usage.MaxTokens, usage.Percentage, usage.AutoCompactEnabled ? usage.AutoCompactThreshold : null, estimated: false),
                ContextBreakdown.From(usage));
        });
    }

    /// <summary>
    /// The indicator without <c>get_context_usage</c>: the main agent's latest call ÷ its model's context window
    /// (DESIGN.md §6, "Per-tab context"). Unknown until a turn has reported the window.
    /// </summary>
    private void ShowEstimate()
    {
        if (_callUsage.ContextPercentage is not { } percentage || _callUsage.ContextWindow is not { } window)
        {
            return;
        }
        var tokens = _callUsage.ContextTokens ?? 0;
        Show(ContextIndicator.From(tokens, window, percentage, _autocompact is { Enabled: true, Threshold: { } threshold } ? threshold : null, estimated: true),
            ContextBreakdown.Estimated(tokens, window));
    }

    /// <summary>Each session event, on the UI thread, before the tab's own handling saves the tab.</summary>
    internal void OnSessionEvent(ClaudeSession session, SessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case AssistantMessageReceived assistant when _callUsage.Add(assistant.Message):
                // A call finished mid-turn: the token count moves on before the result gives the turn's totals.
                TokensShort = TokenTotals.Short(_host.State.Tokens.Total + _callUsage.TurnTokens);
                _host.TurnTokensChanged();
                if (_usageUnavailable)
                {
                    ShowEstimate();
                }
                break;
            case AutocompactStateChanged autocompact:
                _autocompact = autocompact.State;
                break;
            case TurnCompleted completed:
                _host.State.Tokens.Add(completed.Result);
                _callUsage.TurnEnded(completed.Result);
                RefreshTokens();
                _ = RefreshUsageAsync(session);
                break;
            case ConversationReset:
                _callUsage.ContextReset();
                break;
        }
    }

    // ---- Tokens (DESIGN.md §4, "Token stats per tab") ---------------------------------------------------------------

    /// <summary>The tab's tokens, with the running turn's so far: "12.3k tok".</summary>
    [ObservableProperty]
    public partial string TokensShort { get; set; } = "0 tok";

    partial void OnTokensShortChanged(string value) => _host.InfoRowsChanged();

    /// <summary>The running turn's tokens so far, for the working line.</summary>
    public long TurnTokens => _callUsage.TurnTokens;

    public ObservableCollection<TokenRow> TokenRows { get; } = [];

    [ObservableProperty]
    public partial string TokenSummary { get; set; } = "";

    /// <summary>"Clear usage history" with "Also reset per-tab token totals" (DESIGN.md §6).</summary>
    public void ResetTokenTotals()
    {
        _host.State.Tokens = new TokenTotals();
        RefreshTokens();
        _services.SaveState();
    }

    internal void RefreshTokens()
    {
        var totals = _host.State.Tokens;
        TokensShort = TokenTotals.Short(totals.Total + _callUsage.TurnTokens);
        TokenRows.Clear();
        foreach (var (model, t) in totals.Models.OrderByDescending(m => m.Value.Total))
        {
            TokenRows.Add(new TokenRow(_host.ModelDisplayName(model) ?? model, N(t.Input), N(t.Output), N(t.CacheWrite), N(t.CacheRead), $"${t.EstimatedCostUsd:0.00}"));
        }
        TokenSummary = $"{totals.Turns} turn{(totals.Turns == 1 ? "" : "s")} · {totals.Total:N0} tokens · about ${totals.EstimatedCostUsd:0.00} at list price (an estimate, not your bill)";
        RefreshTokenWindow();

        static string N(long n) => n.ToString("N0");
    }

    /// <summary>
    /// This tab's tokens since the current 5-hour window started, the part that counts against the session limit, and
    /// its recent turns for the popover's chart (DESIGN.md §4, "Token stats per tab"). From the usage history.
    /// </summary>
    [ObservableProperty]
    public partial string? TokenWindowText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Controls.ChartPoint> TurnPoints { get; set; } = [];

    [ObservableProperty]
    public partial double TurnChartMaximum { get; set; } = 1;

    /// <summary>The tokens flyout is open: the only place its window and chart show, so they're read only then.</summary>
    public bool IsTokenDetailsOpen
    {
        get;
        set
        {
            field = value;
            if (value)
            {
                RefreshTokenWindow();
            }
        }
    }

    /// <summary>Reads the window's tokens and the chart from the usage history, while the tokens flyout shows them.</summary>
    public void RefreshTokenWindow()
    {
        if (IsTokenDetailsOpen)
        {
            TokenWindowRefresh = RefreshTokenWindowAsync();
        }
    }

    /// <summary>The latest read of the window's tokens, for tests to wait on.</summary>
    internal Task TokenWindowRefresh { get; private set; } = Task.CompletedTask;

    private async Task RefreshTokenWindowAsync()
    {
        if (_services.Usage is not { } usage)
        {
            return;
        }
        var now = _services.Time.GetUtcNow();
        var windowStart = usage.Current?.Session?.ResetsAt is { } resets ? resets - TimeSpan.FromHours(5) : now - TimeSpan.FromHours(5);
        var id = _host.Id;
        try
        {
            var (inWindow, turns) = await Task.Run(() =>
            {
                // One query: the session window is inside the week.
                var week = usage.Store.GetTurns(now - TimeSpan.FromDays(7), now, id);
                var window = week.Where(t => t.Timestamp >= windowStart).Sum(t => t.Total);
                var recent = week
                    .GroupBy(t => t.Timestamp)
                    .Select(g => new Controls.ChartPoint(g.Key, g.Sum(t => t.Total)))
                    .OrderBy(p => p.Time)
                    .TakeLast(40)
                    .ToArray();
                return (window, recent);
            });
            TokenWindowText = $"This session window: {TokenTotals.Short(inWindow)}";
            TurnPoints = turns;
            TurnChartMaximum = turns.Length > 0 ? turns.Max(p => p.Value) : 1;
        }
        catch (Exception)
        {
            // The usage history is optional here.
        }
    }
}
