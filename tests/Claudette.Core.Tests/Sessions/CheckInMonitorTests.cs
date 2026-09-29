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
    private readonly CheckInMonitor _monitor;

    public CheckInMonitorTests()
    {
        _monitor = new CheckInMonitor(_time, () => _settings, _sent.Add, _stuck.Add);
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
    public void Quiet_time_triggers_a_check_in()
    {
        _monitor.TurnStarted();

        Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(["ok?"], _sent);
    }

    [Fact]
    public void Run_time_triggers_a_check_in_even_with_output()
    {
        _settings.QuietTimeMinutes = 0;
        _monitor.TurnStarted();
        for (var i = 0; i < 15; i++)
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
        Advance(TimeSpan.FromMinutes(5));
        _monitor.OutputSeen();
        Advance(TimeSpan.FromMinutes(4));

        Assert.Single(_sent);
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
