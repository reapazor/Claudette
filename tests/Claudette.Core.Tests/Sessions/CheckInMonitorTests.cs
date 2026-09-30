using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Sessions;

public sealed class CheckInMonitorTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly CheckInSettings _settings = new() { RunTimeMinutes = 15, QuietTimeMinutes = 5, Message = "ok?" };
    private readonly List<string> _sent = [];
    private readonly List<bool> _stuck = [];
    private readonly List<CheckInCountdown?> _countdowns = [];
    private readonly CheckInMonitor _monitor;

    public CheckInMonitorTests()
    {
        _monitor = new CheckInMonitor(_time, () => _settings, _sent.Add, _stuck.Add, _countdowns.Add);
    }

    public void Dispose() => _monitor.Dispose();

    [Fact]
    public void Nothing_is_sent_while_output_keeps_coming()
    {
        _monitor.TurnStarted();
        for (var i = 0; i < 8; i++)
        {
            Advance(TimeSpan.FromMinutes(1));
            _monitor.OutputSeen();
        }

        Assert.Empty(_sent);
    }

    [Fact]
    public void Quiet_time_counts_down_to_a_check_in()
    {
        _monitor.TurnStarted();
        var start = _time.GetUtcNow();

        Advance(TimeSpan.FromMinutes(5));

        // Due: it waits out the countdown first, so the user can skip it.
        Assert.Empty(_sent);
        Assert.Equal(new CheckInCountdown(start + TimeSpan.FromMinutes(5) + CheckInMonitor.Countdown, "ok?"), _monitor.PendingCheckIn);

        Advance(CheckInMonitor.Countdown);

        Assert.Equal(["ok?"], _sent);
        Assert.Null(_monitor.PendingCheckIn);
        Assert.Equal(2, _countdowns.Count);
        Assert.Null(_countdowns[^1]);
    }

    [Fact]
    public void Run_time_triggers_a_check_in_even_with_output()
    {
        _settings.QuietTimeMinutes = 0;
        _monitor.TurnStarted();
        for (var i = 0; i < 16; i++)
        {
            Advance(TimeSpan.FromMinutes(1));
            _monitor.OutputSeen();
        }

        Assert.Single(_sent);
    }

    [Fact]
    public void A_reply_restarts_the_timers()
    {
        _monitor.TurnStarted();
        Advance(TimeSpan.FromMinutes(5) + CheckInMonitor.Countdown);
        _monitor.OutputSeen();
        Advance(TimeSpan.FromMinutes(4));

        Assert.Single(_sent);
    }

    [Fact]
    public void Send_now_sends_the_check_in_without_waiting_out_the_countdown()
    {
        _monitor.TurnStarted();
        Advance(TimeSpan.FromMinutes(5));

        _monitor.SendNow();

        Assert.Equal(["ok?"], _sent);
        Assert.Null(_monitor.PendingCheckIn);
        Advance(CheckInMonitor.Countdown);
        Assert.Single(_sent);
    }

    [Fact]
    public void Skipping_the_check_in_sends_nothing_and_the_next_waits_a_whole_interval()
    {
        _monitor.TurnStarted();
        Advance(TimeSpan.FromMinutes(5));

        _monitor.Skip();
        Advance(TimeSpan.FromMinutes(5) - CheckInMonitor.TickInterval);

        Assert.Empty(_sent);
        Assert.Null(_monitor.PendingCheckIn);

        Advance(CheckInMonitor.TickInterval);

        Assert.NotNull(_monitor.PendingCheckIn);
    }

    [Fact]
    public void Output_during_the_countdown_withdraws_a_check_in_that_quiet_time_called_for()
    {
        _monitor.TurnStarted();
        Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(_monitor.PendingCheckIn);

        _monitor.OutputSeen();

        Assert.Null(_monitor.PendingCheckIn);
        Assert.Null(_countdowns[^1]);
        Advance(CheckInMonitor.Countdown);
        Assert.Empty(_sent);
    }

    [Fact]
    public void A_permission_prompt_withdraws_the_countdown()
    {
        _monitor.TurnStarted();
        Advance(TimeSpan.FromMinutes(5));

        _monitor.SetWaitingOnUser(true);
        Advance(CheckInMonitor.Countdown);

        Assert.Null(_monitor.PendingCheckIn);
        Assert.Empty(_sent);
    }

    [Fact]
    public void Two_unanswered_check_ins_mark_the_turn_possibly_stuck_and_stop()
    {
        _monitor.TurnStarted();

        Advance(TimeSpan.FromMinutes(5));
        Advance(TimeSpan.FromMinutes(5));
        Advance(TimeSpan.FromMinutes(5));
        Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(2, _sent.Count);
        Assert.True(_monitor.IsPossiblyStuck);
        Assert.Equal([true], _stuck);

        _monitor.OutputSeen();
        Assert.False(_monitor.IsPossiblyStuck);
    }

    [Fact]
    public void Waiting_on_a_permission_prompt_pauses_check_ins()
    {
        _monitor.TurnStarted();
        _monitor.SetWaitingOnUser(true);

        Advance(TimeSpan.FromMinutes(20));
        _monitor.SetWaitingOnUser(false);
        Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(_sent);
    }

    [Fact]
    public void Disabled_or_idle_sends_nothing()
    {
        _settings.Enabled = false;
        _monitor.TurnStarted();
        Advance(TimeSpan.FromMinutes(30));
        _settings.Enabled = true;
        _monitor.TurnEnded();
        Advance(TimeSpan.FromMinutes(30));

        Assert.Empty(_sent);
    }

    private void Advance(TimeSpan by)
    {
        // Step in tick-sized increments so the timer fires as it would in real time.
        var end = _time.GetUtcNow() + by;
        while (_time.GetUtcNow() < end)
        {
            _time.Advance(CheckInMonitor.TickInterval);
        }
    }
}
