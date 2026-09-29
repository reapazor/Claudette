using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Processes;

/// <summary>
/// Samples a <see cref="ProcessTree"/> on a timer (DESIGN.md §4, Process monitor): every 2 seconds while the Processes
/// panel is visible, every 10 seconds when only the summary shows, and not at all when the monitor is off.
/// <list type="bullet">
/// <item>Samples never overlap: the next one is scheduled when the previous one finishes.</item>
/// <item>After <see cref="Stop"/> or <see cref="Dispose"/> returns, <see cref="Sampled"/> isn't raised again. A sample
/// already under way finishes, and its result is dropped.</item>
/// </list>
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    public static readonly TimeSpan PanelInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10);

    private readonly ProcessTree _tree;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _state = new();
    // Held while Sampled is raised, so Stop can wait for a handler that is running. Always taken before _state.
    private readonly Lock _raise = new();
    private TimeSpan _interval = SummaryInterval;
    private bool _includeCommandLines;
    private ITimer? _timer;
    private int _generation;
    private bool _running;
    private bool _sampling;
    private bool _disposed;
    private long? _lastSampleTimestamp;

    public ProcessSampler(ProcessTree tree, TimeProvider time, ILogger<ProcessSampler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(time);
        _tree = tree;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised with each sample, on a background thread.</summary>
    public event Action<IReadOnlyList<ProcessSnapshot>>? Sampled;

    /// <summary>
    /// Time between samples, <see cref="SummaryInterval"/> by default. Changing it while running reschedules the next
    /// sample, so it's taken at once if the new interval has already passed since the last one.
    /// </summary>
    public TimeSpan Interval
    {
        get
        {
            lock (_state)
            {
                return _interval;
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ITimer? timer = null;
            var due = value;
            lock (_state)
            {
                if (_interval == value)
                {
                    return;
                }
                _interval = value;
                if (_running && !_sampling)
                {
                    timer = _timer;
                    var left = _lastSampleTimestamp is { } last ? value - _time.GetElapsedTime(last) : TimeSpan.Zero;
                    due = left > TimeSpan.Zero ? left : TimeSpan.Zero;
                }
            }
            // Outside the lock: a zero due time can run the sample on this thread.
            Schedule(timer, due);
        }
    }

    /// <summary>Read command lines too. Off when Settings hides them, since they can hold secrets.</summary>
    public bool IncludeCommandLines
    {
        get
        {
            lock (_state)
            {
                return _includeCommandLines;
            }
        }
        set
        {
            lock (_state)
            {
                _includeCommandLines = value;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_state)
            {
                return _running;
            }
        }
    }

    /// <summary>Starts sampling: the first sample is taken straight away, then one every <see cref="Interval"/>.</summary>
    public void Start()
    {
        ITimer timer;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }
            _running = true;
            _lastSampleTimestamp = null;
            var generation = ++_generation;
            timer = _time.CreateTimer(_ => Tick(generation), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer = timer;
        }
        Schedule(timer, TimeSpan.Zero);
    }

    /// <summary>Stops sampling. If a <see cref="Sampled"/> handler is running on another thread, waits for it.</summary>
    public void Stop()
    {
        lock (_raise)
        {
            lock (_state)
            {
                StopLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_raise)
        {
            lock (_state)
            {
                StopLocked();
                _disposed = true;
            }
        }
    }

    private void StopLocked()
    {
        _running = false;
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick(int generation)
    {
        bool includeCommandLines;
        lock (_state)
        {
            if (!_running || generation != _generation || _sampling)
            {
                return;
            }
            _sampling = true;
            includeCommandLines = _includeCommandLines;
        }

        try
        {
            var snapshots = _tree.Sample(includeCommandLines);
            lock (_raise)
            {
                lock (_state)
                {
                    if (!_running || generation != _generation)
                    {
                        return;
                    }
                }
                Sampled?.Invoke(snapshots);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sampling the processes of PID {Pid} failed.", _tree.RootPid);
        }
        finally
        {
            lock (_state)
            {
                _sampling = false;
                _lastSampleTimestamp = _time.GetTimestamp();
                if (_running && generation == _generation)
                {
                    // The interval is never zero, so this can't run a sample on this thread while the lock is held.
                    Schedule(_timer, _interval);
                }
            }
        }
    }

    private static void Schedule(ITimer? timer, TimeSpan due)
    {
        try
        {
            timer?.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Stopped in the meantime.
        }
    }
}
