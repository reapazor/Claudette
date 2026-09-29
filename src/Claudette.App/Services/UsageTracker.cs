using Claudette.Core.Protocol;
using Claudette.Core.Settings;
using Claudette.Usage;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// App-wide plan usage (DESIGN.md §6): polls <c>get_usage</c> through the utility session, takes
/// <c>rate_limit_event</c> updates from any tab, records samples and per-turn tokens in the usage history, and prunes
/// it at launch and once a day.
/// </summary>
public sealed class UsageTracker : IAsyncDisposable
{
    private static readonly TimeSpan UsageCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly AppServices _services;
    private readonly UsagePoller _poller;
    private readonly ITimer _pruneTimer;
    private readonly ILogger _logger;

    public UsageTracker(AppServices services, UsageStore store)
    {
        _services = services;
        Store = store;
        _logger = services.Loggers.CreateLogger<UsageTracker>();
        _poller = new UsagePoller(GetUsageAsync, services.Time, _logger, RunUsageCommandAsync)
        {
            UseUsageCommandFallback = services.Settings.Usage.UseUsageCommandFallback,
        };
        _poller.Updated += OnPolled;
        _pruneTimer = services.Time.CreateTimer(_ => Prune(), null, TimeSpan.Zero, TimeSpan.FromDays(1));
        if (Store.GetLatestSample() is { } latest)
        {
            // The header shows the last known values straight away after a restart, and new readings merge with them.
            Current = latest.ToSnapshot();
            _poller.Restore(Current);
        }
    }

    public UsageStore Store { get; }

    /// <summary>The latest plan usage. Only read on the UI thread.</summary>
    public UsageSnapshot? Current { get; private set; }

    /// <summary>False when <c>get_usage</c> doesn't work and the <c>/usage</c> fallback is off: no model-specific meters.</summary>
    public bool ModelLimitsAvailable => _poller.ModelLimitsAvailable;

    /// <summary>Raised on the UI thread when plan usage changes.</summary>
    public event Action<UsageSnapshot>? Updated;

    /// <summary>Raised on the UI thread after a tab's turn was recorded.</summary>
    public event Action? TurnRecorded;

    public void Start() => _poller.Start();

    public void OnSettingsChanged() => _poller.UseUsageCommandFallback = _services.Settings.Usage.UseUsageCommandFallback;

    /// <summary>A tab finished a turn: record its tokens and poll soon (at most once a minute).</summary>
    public void OnTurnCompleted(string tabId, ResultMessage result)
    {
        var records = TurnRecord.FromResult(result, tabId, _services.Time.GetUtcNow());
        if (records.Count > 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Store.AddTurns(records);
                    _services.Dispatcher.Post(() => TurnRecorded?.Invoke());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Couldn't record a turn's tokens.");
                }
            });
        }
        _poller.NotifyTurnCompleted();
    }

    /// <summary>A <c>rate_limit_event</c> from any tab updates the header at once, between polls.</summary>
    public void OnRateLimitEvent(RateLimitEventMessage message) => _poller.NotifyRateLimitEvent(message.Info);

    private void OnPolled(UsageSnapshot snapshot)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Store.AddSample(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't save a usage sample.");
            }
        });
        _services.Dispatcher.Post(() =>
        {
            Current = snapshot;
            Updated?.Invoke(snapshot);
        });
    }

    private async Task<System.Text.Json.Nodes.JsonObject> GetUsageAsync(CancellationToken cancellationToken)
    {
        var utility = await _services.GetUtilitySessionAsync(cancellationToken).ConfigureAwait(false);
        return await utility.GetUsageAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> RunUsageCommandAsync(CancellationToken cancellationToken)
    {
        var utility = await _services.GetUtilitySessionAsync(cancellationToken).ConfigureAwait(false);
        return await utility.RunLocalCommandAsync("/usage", UsageCommandTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Settings → Usage → Keep usage history.</summary>
    private void Prune()
    {
        try
        {
            Store.Prune(_services.Settings.Usage.KeepHistory.ToTimeSpan());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't prune usage history.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pruneTimer.DisposeAsync().ConfigureAwait(false);
        await _poller.DisposeAsync().ConfigureAwait(false);
    }
}
