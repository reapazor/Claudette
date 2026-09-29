using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Sessions;

/// <summary>DESIGN.md §6, "Continuing after a limit resets".</summary>
public sealed class AutoContinueMonitorTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));
    private readonly List<string> _sent = [];
    private bool _enabled = true;
    private int _changes;
    private readonly AutoContinueMonitor _monitor;

    public AutoContinueMonitorTests()
    {
        _monitor = new AutoContinueMonitor(_time, () => _enabled, _sent.Add, () => _changes++);
    }

    public void Dispose() => _monitor.Dispose();

    private DateTimeOffset Reset => _time.GetUtcNow() + TimeSpan.FromHours(2);

    [Fact]
    public void A_turn_stopped_at_the_limit_continues_shortly_after_the_reset()
    {
        var reset = Reset;
        StopAtLimit(reset);

        var wait = Assert.IsType<LimitWait>(_monitor.Wait);
        Assert.True(wait.WillContinue);
        Assert.Equal(reset, wait.ResetsAt);
        Assert.Equal("session limit", wait.LimitName);

        AdvanceTo(reset);
        Assert.Empty(_sent);
        AdvanceTo(reset + AutoContinueMonitor.Grace);

        Assert.Equal([AutoContinueMonitor.Message], _sent);
        Assert.Null(_monitor.Wait);
    }

    [Fact]
    public void The_rejection_can_come_after_the_result()
    {
        _monitor.TurnStarted();
        _monitor.TurnEnded(LimitResult());
        Assert.Null(_monitor.Wait);

        _monitor.RateLimit(Rejected(Reset));

        Assert.True(_monitor.Wait?.WillContinue);
    }

    [Fact]
    public void A_rejection_the_turn_got_past_is_forgotten()
    {
        // Extra usage took over, say: the turn went on and finished.
        _monitor.TurnStarted();
        _monitor.RateLimit(Rejected(Reset));
        _monitor.TurnEnded(Result(isError: false));
        _monitor.TurnStarted();
        _monitor.TurnEnded(LimitResult());

        Assert.Null(_monitor.Wait);
    }

    [Fact]
    public void Only_a_limit_that_waiting_fixes_counts()
    {
        _monitor.TurnStarted();
        _monitor.RateLimit(Rejected(Reset, errorCode: "credits_required"));
        _monitor.TurnEnded(LimitResult());
        Assert.Null(_monitor.Wait);

        // A server error, a stopped turn and one out of turns aren't the limit, whatever the rejection says.
        foreach (var result in new[] { Result(isError: true, status: 529), Result(isError: true, terminalReason: "aborted_streaming"), Result(isError: true, terminalReason: "max_turns") })
        {
            _monitor.TurnStarted();
            _monitor.RateLimit(Rejected(Reset));
            _monitor.TurnEnded(result);
            Assert.Null(_monitor.Wait);
        }
    }

    [Fact]
    public void A_turn_that_starts_ends_the_wait()
    {
        StopAtLimit(Reset);

        _monitor.TurnStarted();
        AdvanceTo(Reset + TimeSpan.FromHours(1));

        Assert.Null(_monitor.Wait);
        Assert.Empty(_sent);
    }

    [Fact]
    public void Turned_off_it_says_when_the_limit_resets_and_continues_only_when_asked()
    {
        _enabled = false;
        var reset = Reset;
        StopAtLimit(reset);
        Assert.Equal(LimitWaitHold.TurnedOff, _monitor.Wait?.Hold);

        AdvanceTo(reset + TimeSpan.FromMinutes(5));
        Assert.Empty(_sent);
        Assert.True(_monitor.Wait?.HasReset);

        _monitor.ContinueAtReset();

        Assert.Equal([AutoContinueMonitor.Message], _sent);
        Assert.Null(_monitor.Wait);
    }

    [Fact]
    public void Turning_it_on_or_off_while_waiting_changes_the_wait()
    {
        StopAtLimit(Reset);

        _enabled = false;
        _monitor.SettingsChanged();
        Assert.Equal(LimitWaitHold.TurnedOff, _monitor.Wait?.Hold);

        _enabled = true;
        _monitor.SettingsChanged();
        Assert.True(_monitor.Wait?.WillContinue);
    }

    [Fact]
    public void A_reset_more_than_a_day_away_waits_only_when_asked()
    {
        var reset = _time.GetUtcNow() + TimeSpan.FromDays(3);
        StopAtLimit(reset, "seven_day");
        Assert.Equal(LimitWaitHold.TooFar, _monitor.Wait?.Hold);
        Assert.Equal("weekly limit", _monitor.Wait?.LimitName);

        _monitor.ContinueAtReset();
        Assert.True(_monitor.Wait?.WillContinue);
        AdvanceTo(reset + AutoContinueMonitor.Grace);

        Assert.Equal([AutoContinueMonitor.Message], _sent);
    }

    [Fact]
    public void Dont_continue_holds_for_that_reset_only()
    {
        var reset = Reset;
        StopAtLimit(reset);

        _monitor.DontContinue();
        Assert.Equal(LimitWaitHold.Cancelled, _monitor.Wait?.Hold);

        // The user tries again before the reset, and hits the limit again: still no automatic continue.
        _monitor.UserSent();
        StopAtLimit(reset);
        Assert.Equal(LimitWaitHold.Cancelled, _monitor.Wait?.Hold);
        AdvanceTo(reset + TimeSpan.FromMinutes(5));
        Assert.Empty(_sent);

        // The next window starts fresh.
        _monitor.UserSent();
        StopAtLimit(Reset);
        Assert.True(_monitor.Wait?.WillContinue);
    }

    [Fact]
    public void Dismiss_closes_a_wait_that_wont_continue()
    {
        StopAtLimit(Reset);
        _monitor.Dismiss();
        Assert.NotNull(_monitor.Wait);

        _monitor.DontContinue();
        _monitor.Dismiss();

        Assert.Null(_monitor.Wait);
    }

    [Fact]
    public void Continuing_into_the_limit_again_stops_after_two_repeats()
    {
        for (var hit = 0; hit <= AutoContinueMonitor.MaxRepeats; hit++)
        {
            var reset = Reset;
            StopAtLimit(reset);
            Assert.True(_monitor.Wait?.WillContinue, $"hit {hit + 1}");
            AdvanceTo(reset + AutoContinueMonitor.Grace);
        }
        Assert.Equal(AutoContinueMonitor.MaxRepeats + 1, _sent.Count);

        StopAtLimit(Reset);

        Assert.Equal(LimitWaitHold.Repeated, _monitor.Wait?.Hold);
    }

    [Fact]
    public void A_continue_that_gets_going_resets_the_count()
    {
        for (var round = 0; round < 3; round++)
        {
            var reset = Reset;
            StopAtLimit(reset);
            Assert.True(_monitor.Wait?.WillContinue, $"round {round + 1}");
            AdvanceTo(reset + AutoContinueMonitor.Grace);
            _monitor.TurnStarted();
            _monitor.TurnEnded(Result(isError: false));
        }
    }

    [Fact]
    public void A_reset_long_missed_waits_for_the_user()
    {
        var reset = Reset;
        // Saved with the tab before Claudette quit, and restored long after the reset.
        _time.SetUtcNow(reset + TimeSpan.FromHours(3));

        _monitor.Restore(new LimitWait(reset, "five_hour", reset + AutoContinueMonitor.Grace, LimitWaitHold.None));

        Assert.Empty(_sent);
        Assert.Equal(LimitWaitHold.Missed, _monitor.Wait?.Hold);
        Assert.True(_monitor.Wait?.HasReset);
    }

    [Fact]
    public void A_restored_wait_still_continues_at_the_reset()
    {
        var reset = Reset;
        _monitor.Restore(new LimitWait(reset, "five_hour", reset + AutoContinueMonitor.Grace, LimitWaitHold.None));
        Assert.True(_monitor.Wait?.WillContinue);

        AdvanceTo(reset + AutoContinueMonitor.Grace);

        Assert.Equal([AutoContinueMonitor.Message], _sent);
    }

    [Fact]
    public void A_reset_that_already_passed_continues_shortly()
    {
        // The computer's clock runs ahead of the server's.
        StopAtLimit(_time.GetUtcNow() - TimeSpan.FromMinutes(2));
        Assert.Empty(_sent);

        AdvanceTo(_time.GetUtcNow() + AutoContinueMonitor.Grace);

        Assert.Equal([AutoContinueMonitor.Message], _sent);
    }

    [Fact]
    public void Changes_are_reported()
    {
        StopAtLimit(Reset);
        _monitor.DontContinue();
        _monitor.Dismiss();

        Assert.Equal(3, _changes);
    }

    [Fact]
    public void Reads_the_reset_time_and_limit_from_the_rejection()
    {
        var info = JsonNode.Parse("""{"status":"rejected","resetsAt":1790648400,"rateLimitType":"seven_day_opus","overageStatus":"rejected"}""")!.AsObject();

        var rejection = AutoContinueMonitor.Rejection(info);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790648400), rejection?.ResetsAt);
        Assert.Equal("seven_day_opus", rejection?.LimitType);
        Assert.Null(AutoContinueMonitor.Rejection(JsonNode.Parse("""{"status":"allowed_warning","resetsAt":1790648400}""")!.AsObject()));
        Assert.Null(AutoContinueMonitor.Rejection(JsonNode.Parse("""{"status":"rejected"}""")!.AsObject()));
    }

    /// <summary>What Claude Code sends when a turn runs into the limit: the rejection, then the failed turn.</summary>
    private void StopAtLimit(DateTimeOffset reset, string limitType = "five_hour")
    {
        _monitor.TurnStarted();
        _monitor.RateLimit(Rejected(reset, limitType));
        _monitor.TurnEnded(LimitResult());
    }

    /// <summary>Moves the clock on a tick at a time, as it passes while the computer is awake.</summary>
    private void AdvanceTo(DateTimeOffset time)
    {
        while (_time.GetUtcNow() < time)
        {
            var step = time - _time.GetUtcNow();
            _time.Advance(step < AutoContinueMonitor.TickInterval ? step : AutoContinueMonitor.TickInterval);
        }
        _monitor.Tick();
    }

    private static JsonObject Rejected(DateTimeOffset reset, string limitType = "five_hour", string? errorCode = null)
    {
        var info = new JsonObject { ["status"] = "rejected", ["resetsAt"] = reset.ToUnixTimeSeconds(), ["rateLimitType"] = limitType };
        if (errorCode is not null)
        {
            info["errorCode"] = errorCode;
        }
        return Parsed(info);
    }

    /// <summary>As read off Claude Code's output, rather than built in code.</summary>
    private static JsonObject Parsed(JsonObject json) => JsonNode.Parse(json.ToJsonString())!.AsObject();

    private static ResultMessage LimitResult() => Result(isError: true, status: 429, terminalReason: "api_error");

    private static ResultMessage Result(bool isError, int? status = null, string? terminalReason = null)
    {
        var raw = new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = isError };
        if (status is not null)
        {
            raw["api_error_status"] = status;
        }
        return new ResultMessage("success", isError, "You've hit your session limit · resets 2pm", terminalReason, "s1", null, null, null, Parsed(raw));
    }
}
