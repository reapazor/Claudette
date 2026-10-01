using Claudette.App.Services;

namespace Claudette.App.ViewModels;

/// <summary>
/// Something that happens every <c>interval</c> on the UI thread while it runs, such as a running time ticking each
/// second, timed by the injected clock.
/// </summary>
internal sealed class UiTicker(TimeProvider time, IUiDispatcher dispatcher, TimeSpan interval, Action tick) : IDisposable
{
    private ITimer? _timer;

    public bool IsRunning => _timer is not null;

    /// <summary>Starts ticking, if it wasn't already, or stops.</summary>
    public void Run(bool run)
    {
        if (!run)
        {
            Stop();
            return;
        }
        if (_timer is null)
        {
            ITimer? timer = null;
            timer = time.CreateTimer(_ => dispatcher.Post(() =>
            {
                // A tick queued just before Stop doesn't run.
                if (ReferenceEquals(_timer, timer))
                {
                    tick();
                }
            }), null, interval, interval);
            _timer = timer;
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Something that happens once on the UI thread after a delay, unless it is restarted or cancelled first: a search
/// that waits for typing to pause, or a "Copied" that goes back. Timed by the injected clock.
/// </summary>
internal sealed class UiTimeout(TimeProvider time, IUiDispatcher dispatcher) : IDisposable
{
    private ITimer? _timer;

    /// <summary>Waiting to run.</summary>
    public bool IsPending => _timer is not null;

    /// <summary>Runs <paramref name="action"/> after <paramref name="delay"/>, in place of whatever was waiting.</summary>
    public void Restart(TimeSpan delay, Action action)
    {
        _timer?.Dispose();
        ITimer? timer = null;
        timer = time.CreateTimer(_ => dispatcher.Post(() =>
        {
            // Only the latest restart runs, and not after Cancel.
            if (timer is not null && ReferenceEquals(_timer, timer))
            {
                _timer = null;
                timer.Dispose();
                action();
            }
        }), null, delay, Timeout.InfiniteTimeSpan);
        _timer = timer;
    }

    public void Cancel()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Cancel();
}
