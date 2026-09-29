using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Usage;

/// <summary>
/// Keeps the app-wide plan usage current (DESIGN.md §6, "Sampling").
/// <list type="bullet">
/// <item>Polls <c>get_usage</c> on <see cref="Start"/>, then every 5 minutes after the last poll.</item>
/// <item>After a turn, polls again, at most once a minute.</item>
/// <item><c>rate_limit_event</c>s update the session and weekly readings at once, between polls.</item>
/// <item>Every reading merges with the current ones (<see cref="UsageParser.Merge"/>), so a stale one doesn't move a
/// meter back.</item>
/// <item>If <c>get_usage</c> fails or changes shape, keeps going on <c>rate_limit_event</c>s and reports the
/// model-specific limits as unavailable, unless the <c>/usage</c> fallback is on and works. It keeps trying
/// <c>get_usage</c> on the normal schedule and recovers when it works again.</item>
/// </list>
/// Polls never overlap. All timing goes through the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class UsagePoller : IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumTurnInterval = TimeSpan.FromMinutes(1);

    private readonly Func<CancellationToken, Task<JsonObject>> _getUsage;
    private readonly Func<CancellationToken, Task<string?>>? _usageCommand;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();

    // Serializes updates and their notifications, so handlers see them in order. Taken before _lock, never after.
    private readonly Lock _publishLock = new();

    private UsageSnapshot? _current;
    private bool _started;
    private bool _disposed;
    private bool _polling;
    private DateTimeOffset? _lastPollStart;
    private DateTimeOffset? _due;
    private TaskCompletionSource _pollDone = CompletedSource();
    private bool _modelLimitsAvailable = true;
    private bool _useUsageCommandFallback;

    /// <param name="getUsage">Sends <c>get_usage</c> (for example through the utility session) and returns the response.</param>
    /// <param name="usageCommand">
    /// Runs <c>/usage</c> and returns its text: the last-resort fallback, used only while
    /// <see cref="UseUsageCommandFallback"/> is on.
    /// </param>
    public UsagePoller(
        Func<CancellationToken, Task<JsonObject>> getUsage,
        TimeProvider time,
        ILogger? logger = null,
        Func<CancellationToken, Task<string?>>? usageCommand = null)
    {
        _getUsage = getUsage;
        _usageCommand = usageCommand;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
        _timer = time.CreateTimer(_ => Evaluate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A new snapshot. Raised on whatever thread produced it; handlers must not block.</summary>
    public event Action<UsageSnapshot>? Updated;

    public UsageSnapshot? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>False while <c>get_usage</c> is failing and no fallback supplies the model-specific limits.</summary>
    public bool ModelLimitsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _modelLimitsAvailable;
            }
        }
    }

    /// <summary>Settings → Usage: parse <c>/usage</c> when <c>get_usage</c> fails. Off by default.</summary>
    public bool UseUsageCommandFallback
    {
        get
        {
            lock (_lock)
            {
                return _useUsageCommandFallback;
            }
        }
        set
        {
            lock (_lock)
            {
                _useUsageCommandFallback = value;
            }
        }
    }

    /// <summary>Completes when the poll in progress (if any) finishes.</summary>
    internal Task PollCompletion
    {
        get
        {
            lock (_lock)
            {
                return _pollDone.Task;
            }
        }
    }

    /// <summary>
    /// The latest stored sample, after a restart. The first readings merge with it, so a stale one doesn't move the
    /// meters below what was shown before the restart. Not published, and ignored once there's a reading.
    /// </summary>
    public void Restore(UsageSnapshot snapshot)
    {
        lock (_lock)
        {
            _current ??= snapshot;
        }
    }

    /// <summary>Polls now, and then every <see cref="PollInterval"/>.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }
            _started = true;
            _due = _time.GetUtcNow();
        }
        Evaluate();
    }

    /// <summary>
    /// A turn finished in some tab. Polls at once if the last poll was at least a minute ago; otherwise once, when
    /// the minute is up. Ignored before <see cref="Start"/>.
    /// </summary>
    public void NotifyTurnCompleted()
    {
        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }
            var now = _time.GetUtcNow();
            var earliest = _lastPollStart is { } last && last + MinimumTurnInterval > now ? last + MinimumTurnInterval : now;
            if (_due is not { } due || earliest < due)
            {
                _due = earliest;
            }
        }
        Evaluate();
    }

    /// <summary>
    /// A <c>rate_limit_event</c> from any tab: its <c>rate_limit_info</c>. Updates the session and weekly readings at
    /// once and keeps the model-specific ones.
    /// </summary>
    public void NotifyRateLimitEvent(JsonObject rateLimitInfo)
    {
        if (UsageParser.FromRateLimitEvent(rateLimitInfo, _time.GetUtcNow()) is { } update)
        {
            Publish(current => UsageParser.Merge(current, update));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _stopping.Cancel();
        _timer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await PollCompletion.ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Starts a poll if one is due and none is running; otherwise sets the timer for the next one.</summary>
    private void Evaluate()
    {
        TaskCompletionSource done;
        lock (_lock)
        {
            if (_disposed || !_started || _polling || _due is not { } due)
            {
                // A running poll calls this again when it finishes.
                return;
            }
            var now = _time.GetUtcNow();
            if (due > now)
            {
                _timer.Change(due - now, Timeout.InfiniteTimeSpan);
                return;
            }
            _polling = true;
            _lastPollStart = now;
            _due = now + PollInterval;
            _pollDone = done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RunPollAsync(done);
    }

    private async Task RunPollAsync(TaskCompletionSource done)
    {
        try
        {
            await PollAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Usage poll failed.");
        }
        finally
        {
            lock (_lock)
            {
                _polling = false;
            }
            try
            {
                // A poll that fell due meanwhile starts before this one reports done.
                Evaluate();
            }
            finally
            {
                done.TrySetResult();
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        UsageSnapshot? snapshot = null;
        try
        {
            var response = await _getUsage(cancellationToken).ConfigureAwait(false);
            snapshot = UsageParser.FromGetUsage(response, _time.GetUtcNow());
            if (snapshot is null)
            {
                _logger.LogWarning("get_usage returned no usable limits; using rate_limit_event only.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "get_usage failed; using rate_limit_event only.");
        }

        if (snapshot is null && UseUsageCommandFallback && _usageCommand is not null)
        {
            try
            {
                var text = await _usageCommand(cancellationToken).ConfigureAwait(false);
                snapshot = text is null ? null : UsageParser.FromUsageCommand(text, _time.GetUtcNow());
                if (snapshot is null)
                {
                    _logger.LogWarning("Couldn't read the /usage output.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The /usage fallback failed.");
            }
        }

        if (snapshot is not null)
        {
            lock (_lock)
            {
                _modelLimitsAvailable = true;
            }
            Publish(current => UsageParser.Merge(current, snapshot));
            return;
        }

        lock (_lock)
        {
            _modelLimitsAvailable = false;
        }
        // Model readings from an earlier poll would go stale: drop them rather than show old numbers. A restored
        // sample is only what the header shows until Claude Code reports, so it isn't published again.
        Publish(current => current is { Source: not UsageSource.Stored, WeeklyModels.Count: > 0 }
            ? current with { Limits = [.. current.Limits.Where(l => l.Kind != LimitKind.WeeklyModel)] }
            : null);
    }

    /// <summary>Applies <paramref name="update"/> to the current snapshot (null: no change) and notifies.</summary>
    private void Publish(Func<UsageSnapshot?, UsageSnapshot?> update)
    {
        lock (_publishLock)
        {
            UsageSnapshot? snapshot;
            lock (_lock)
            {
                if (_disposed || update(_current) is not { } next)
                {
                    return;
                }
                _current = snapshot = next;
            }
            try
            {
                Updated?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A usage update handler failed.");
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
