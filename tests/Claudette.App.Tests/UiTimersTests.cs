using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.App.Tests;

/// <summary>The tickers and timeouts the view models share, timed by the injected clock.</summary>
public class UiTimersTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InlineDispatcher _dispatcher = new();

    [Fact]
    public void A_ticker_ticks_each_interval_while_it_runs()
    {
        var ticks = 0;
        using var ticker = new UiTicker(_time, _dispatcher, TimeSpan.FromSeconds(1), () => ticks++);

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, ticks);

        ticker.Run(true);
        Assert.True(ticker.IsRunning);
        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, ticks);

        // Running again doesn't start a second timer.
        ticker.Run(true);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, ticks);

        ticker.Run(false);
        Assert.False(ticker.IsRunning);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(3, ticks);
    }

    [Fact]
    public void A_timeout_runs_once_after_its_delay()
    {
        var runs = 0;
        using var timeout = new UiTimeout(_time, _dispatcher);

        timeout.Restart(TimeSpan.FromMilliseconds(500), () => runs++);
        Assert.True(timeout.IsPending);
        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(0, runs);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, runs);
        Assert.False(timeout.IsPending);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Restarting_a_timeout_runs_only_the_latest_and_waits_its_whole_delay()
    {
        var ran = new List<string>();
        using var timeout = new UiTimeout(_time, _dispatcher);

        timeout.Restart(TimeSpan.FromMilliseconds(500), () => ran.Add("first"));
        _time.Advance(TimeSpan.FromMilliseconds(400));
        timeout.Restart(TimeSpan.FromMilliseconds(500), () => ran.Add("second"));
        _time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Empty(ran);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(["second"], ran);
    }

    [Fact]
    public void A_cancelled_timeout_doesnt_run()
    {
        var runs = 0;
        using var timeout = new UiTimeout(_time, _dispatcher);

        timeout.Restart(TimeSpan.FromMilliseconds(500), () => runs++);
        timeout.Cancel();
        Assert.False(timeout.IsPending);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, runs);
    }
}
