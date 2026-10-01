using Claudette.Core.Protocol;
using Claudette.Core.Settings;
using Claudette.Usage;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// App-wide plan usage (DESIGN.md §6): polls <c>get_usage</c> through the utility session, takes
/// <c>rate_limit_event</c> updates from any tab, records samples and per-turn tokens in the usage history, and prunes
/// it at launch and once a day. With sharing on, it also shares the samples through the session library and imports
/// the other machines' ("Sharing across machines").
/// </summary>
public sealed class UsageTracker : IAsyncDisposable
{
    /// <summary>How often the other machines' shared usage is read, as settings sync reads its file.</summary>
    public static readonly TimeSpan ShareInterval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan UsageCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly AppServices _services;
    private readonly UsagePoller _poller;
    private readonly ITimer _pruneTimer;
    private readonly ITimer _shareTimer;
    private readonly ILogger _logger;
    private readonly Lock _writeLock = new();
    private readonly SemaphoreSlim _sharing = new(1, 1);
    private readonly string _machineId;
    private Task _historyWrites = Task.CompletedTask;
    private bool _shares;
    private string? _sharedTo;
    // Machines whose file a later Claudette wrote in a format this one can't read, logged once each. Used under _sharing.
    private readonly HashSet<string> _newerFormats = new(StringComparer.Ordinal);
    private volatile bool _disposed;

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
        if (services.State.MachineId is not { Length: > 0 } machineId)
        {
            machineId = services.State.MachineId = Guid.NewGuid().ToString("N");
            services.SaveState();
        }
        _machineId = machineId;
        _shares = services.Settings.Usage.ShareThroughLibrary;
        services.SettingsChanged += OnServicesSettingsChanged;
        // At launch, then once a minute.
        _shareTimer = services.Time.CreateTimer(_ => _ = ShareAsync(), null, TimeSpan.Zero, ShareInterval);
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

    /// <summary>Raised on the UI thread after readings another machine shared were added to the history.</summary>
    public event Action? SamplesImported;

    public void Start() => _poller.Start();

    public void OnSettingsChanged()
    {
        _poller.UseUsageCommandFallback = _services.Settings.Usage.UseUsageCommandFallback;
        // Turned on: share straight away rather than at the next reading.
        var shares = _services.Settings.Usage.ShareThroughLibrary;
        if (shares && !_shares)
        {
            _ = Task.Run(() => ShareAsync());
        }
        _shares = shares;
    }

    private void OnServicesSettingsChanged(object? sender, EventArgs e) => OnSettingsChanged();

    /// <summary>
    /// Settings → Usage → Share usage with my other machines (DESIGN.md §6, "Sharing across machines"): writes this
    /// machine's readings of the last week to its file in the library when they changed or the library moved, and adds
    /// the readings other machines signed in to the same account shared to the history. Only plan usage readings: never
    /// token records or tab names.
    /// </summary>
    /// <param name="publish">This machine has a new reading to share.</param>
    public async Task ShareAsync(bool publish = false)
    {
        var account = _services.RemoteControl.Account;
        if (_disposed || !_services.Settings.Usage.ShareThroughLibrary || UsageSharing.AccountKey(account?.Email, account?.OrganizationName) is not { } key)
        {
            return;
        }
        var library = _services.Library.Library;
        var machineName = _services.Library.MachineName;
        var keep = _services.Settings.Usage.KeepHistory.ToTimeSpan();
        await _sharing.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            var now = _services.Time.GetUtcNow();
            if (publish || _sharedTo != library.LibraryFolder)
            {
                var json = UsageSharing.Write(machineName, _services.AppVersion.ToString(), key, now, Store.GetOwnSamples(now - UsageSharing.SharedPeriod));
                await library.WriteSharedUsageAsync(_machineId, json).ConfigureAwait(false);
                _sharedTo = library.LibraryFolder;
            }
            var imported = 0;
            foreach (var (machineId, text) in await Task.Run(() => library.ReadSharedUsage(_machineId)).ConfigureAwait(false))
            {
                // Another account's readings would say nothing about this one's limits.
                if (UsageSharing.Read(text) is not { } shared || shared.Account != key)
                {
                    continue;
                }
                try
                {
                    imported += Import(machineId, shared, keep, now);
                }
                catch (Exception ex) when (ex is not ObjectDisposedException)
                {
                    // One machine's file must not keep the others' out.
                    _logger.LogWarning(ex, "Couldn't add the usage {Machine} shared.", shared.MachineName);
                }
            }
            if (imported > 0)
            {
                var latest = Store.GetLatestSample()?.ToSnapshot();
                _services.Dispatcher.Post(() =>
                {
                    // The header shows the newest reading any machine took, as it does after a restart.
                    if (latest is not null && (Current is null || latest.AsOf > Current.AsOf))
                    {
                        Current = latest;
                        _poller.Restore(latest);
                    }
                    SamplesImported?.Invoke();
                });
            }
        }
        catch (Exception ex)
        {
            // The library folder may be unavailable for now (a sync client not running, a drive not mounted).
            _logger.LogWarning(ex, "Couldn't share usage through the session library.");
        }
        finally
        {
            _sharing.Release();
        }
    }

    /// <summary>Adds one machine's shared readings to the history; returns how many were new.</summary>
    private int Import(string machineId, SharedUsage shared, TimeSpan? keep, DateTimeOffset now)
    {
        if (shared.IsNewerFormat)
        {
            if (_newerFormats.Add(machineId))
            {
                _logger.LogWarning("Left out the usage {Machine} shared: Claudette {WrittenBy} wrote it in format {Version}, and this version reads up to {Known}. Update Claudette here to include it.",
                    shared.MachineName, shared.WrittenBy ?? "(unknown)", shared.Version, UsageSharing.FileVersion);
            }
            return 0;
        }
        _newerFormats.Remove(machineId);
        return Store.ImportSamples(machineId, keep is { } k ? shared.Samples.Where(sample => sample.Timestamp >= now - k) : shared.Samples);
    }

    /// <summary>A tab finished a turn: record its tokens and its name, and poll soon (at most once a minute).</summary>
    /// <param name="project">The tab's folder, for usage by project.</param>
    public void OnTurnCompleted(string tabId, string tabName, ResultMessage result, string? project = null)
    {
        var records = TurnRecord.FromResult(result, tabId, _services.Time.GetUtcNow(), project);
        if (records.Count > 0)
        {
            WriteHistory(() =>
            {
                try
                {
                    Store.AddTurns(records);
                    Store.SetTabName(tabId, tabName);
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

    /// <summary>A tab's name changed: the history keeps the latest, so the tab's row is still named once it's closed.</summary>
    public void OnTabRenamed(string tabId, string tabName) =>
        WriteHistory(() =>
        {
            try
            {
                Store.SetTabName(tabId, tabName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't record a tab's name.");
            }
        });

    /// <summary>
    /// Writes turns and names off the UI thread, one at a time in the order they came, so a name can't land before the
    /// turn it belongs to, or an older name after a newer one.
    /// </summary>
    private void WriteHistory(Action write)
    {
        lock (_writeLock)
        {
            _historyWrites = _historyWrites.ContinueWith(_ => write(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>A <c>rate_limit_event</c> from any tab updates the header at once, between polls.</summary>
    public void OnRateLimitEvent(RateLimitEventMessage message) => _poller.NotifyRateLimitEvent(message.Info);

    private void OnPolled(UsageSnapshot snapshot)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (Store.AddSample(snapshot))
                {
                    await ShareAsync(publish: true).ConfigureAwait(false);
                }
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
        _services.SettingsChanged -= OnServicesSettingsChanged;
        await _pruneTimer.DisposeAsync().ConfigureAwait(false);
        await _shareTimer.DisposeAsync().ConfigureAwait(false);
        await _poller.DisposeAsync().ConfigureAwait(false);
        // A share under way finishes before the history closes; one waiting its turn then sees it's closing.
        _disposed = true;
        await _sharing.WaitAsync().ConfigureAwait(false);
        _sharing.Release();
        // The last turns and names are written before the history closes. Each write catches its own failure.
        Task writes;
        lock (_writeLock)
        {
            writes = _historyWrites;
        }
        await writes.ConfigureAwait(false);
    }
}
