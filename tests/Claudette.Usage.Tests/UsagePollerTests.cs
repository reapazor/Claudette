using System.Text.Json.Nodes;
using Claudette.Usage.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Usage.Tests;

public sealed class UsagePollerTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(UsageFixtures.RecordedAt);
    private readonly FakeUsageSource _source = new();
    private readonly List<UsageSnapshot> _updates = [];
    private readonly UsagePoller _poller;

    public UsagePollerTests()
    {
        _poller = new UsagePoller(_source.GetUsageAsync, _time, usageCommand: _source.UsageCommandAsync);
        _poller.Updated += _updates.Add;
    }

    public ValueTask DisposeAsync() => _poller.DisposeAsync();

    [Fact]
    public void Start_polls_at_once()
    {
        _poller.Start();

        Assert.Equal(1, _source.Calls);
        var update = Assert.Single(_updates);
        Assert.Equal(UsageSource.GetUsage, update.Source);
        Assert.Same(update, _poller.Current);
        Assert.Single(update.WeeklyModels);
        Assert.True(_poller.ModelLimitsAvailable);
    }

    [Fact]
    public void Nothing_is_polled_before_start()
    {
        _poller.NotifyTurnCompleted();
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, _source.Calls);
        Assert.Null(_poller.Current);
    }

    [Fact]
    public void It_polls_every_five_minutes()
    {
        _poller.Start();

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _source.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _source.Calls);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3, _source.Calls);
        Assert.Equal(3, _updates.Count);
    }

    [Fact]
    public void A_finished_turn_polls_at_once_when_the_last_poll_was_a_minute_ago()
    {
        _poller.Start();
        _time.Advance(TimeSpan.FromMinutes(2));

        _poller.NotifyTurnCompleted();
        Assert.Equal(2, _source.Calls);

        // The five minutes restart from that poll.
        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, _source.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, _source.Calls);
    }

    [Fact]
    public void Finished_turns_within_the_minute_make_one_poll_when_it_is_up()
    {
        _poller.Start();
        _time.Advance(TimeSpan.FromSeconds(20));

        _poller.NotifyTurnCompleted();
        _poller.NotifyTurnCompleted();
        _time.Advance(TimeSpan.FromSeconds(10));
        _poller.NotifyTurnCompleted();
        Assert.Equal(1, _source.Calls);

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, _source.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _source.Calls);

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, _source.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, _source.Calls);
    }

    [Fact]
    public void A_rate_limit_event_publishes_at_once_and_keeps_the_model_readings()
    {
        _poller.Start();
        _time.Advance(TimeSpan.FromSeconds(30));

        _poller.NotifyRateLimitEvent(UsageFixtures.RateLimitInfo());

        Assert.Equal(1, _source.Calls);
        Assert.Equal(2, _updates.Count);
        var current = _poller.Current!;
        Assert.Same(_updates[^1], current);
        Assert.Equal(UsageSource.RateLimitEvent, current.Source);
        Assert.Equal(_time.GetUtcNow(), current.AsOf);
        Assert.Equal(12, current.Session!.Percent);
        Assert.Equal(57, current.WeeklyAll!.Percent);
        Assert.Equal("Fable", Assert.Single(current.WeeklyModels).Label);
    }

    [Fact]
    public void A_rate_limit_event_before_any_poll_is_published()
    {
        _poller.NotifyRateLimitEvent(UsageFixtures.RateLimitInfo());
        _poller.NotifyRateLimitEvent([]);

        var update = Assert.Single(_updates);
        Assert.Equal(2, update.Limits.Count);
    }

    [Fact]
    public void When_get_usage_fails_it_falls_back_to_rate_limit_events_and_recovers()
    {
        _poller.Start();
        _source.Respond = _ => throw new TimeoutException("Claude Code didn't answer the 'get_usage' control request.");

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(_poller.ModelLimitsAvailable);
        Assert.Empty(_poller.Current!.WeeklyModels);
        Assert.Equal(2, _updates.Count);

        _poller.NotifyRateLimitEvent(UsageFixtures.RateLimitInfo());
        Assert.Equal(12, _poller.Current!.Session!.Percent);
        Assert.Empty(_poller.Current.WeeklyModels);

        // It keeps trying on the normal schedule.
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3, _source.Calls);
        Assert.False(_poller.ModelLimitsAvailable);
        Assert.Equal(0, _source.UsageCommandCalls);

        _source.Respond = _ => Task.FromResult(UsageFixtures.GetUsage());
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(_poller.ModelLimitsAvailable);
        Assert.Equal(UsageSource.GetUsage, _poller.Current!.Source);
        Assert.Single(_poller.Current.WeeklyModels);
    }

    [Fact]
    public void An_unexpected_get_usage_shape_counts_as_a_failure()
    {
        _source.Respond = _ => Task.FromResult(new JsonObject { ["usage_v2"] = new JsonObject() });

        _poller.Start();

        Assert.False(_poller.ModelLimitsAvailable);
        Assert.Null(_poller.Current);
        Assert.Empty(_updates);
    }

    [Fact]
    public void The_usage_command_fallback_supplies_the_model_readings_when_turned_on()
    {
        _source.Respond = _ => throw new InvalidOperationException("gone");
        _poller.UseUsageCommandFallback = true;

        _poller.Start();

        Assert.Equal(1, _source.UsageCommandCalls);
        Assert.True(_poller.ModelLimitsAvailable);
        Assert.Equal(UsageSource.UsageCommand, _poller.Current!.Source);
        Assert.Equal("Fable", Assert.Single(_poller.Current.WeeklyModels).Label);

        // A rate_limit_event keeps them too.
        _poller.NotifyRateLimitEvent(UsageFixtures.RateLimitInfo());
        Assert.Single(_poller.Current!.WeeklyModels);
    }

    [Fact]
    public void A_failing_usage_command_fallback_leaves_the_model_readings_unavailable()
    {
        _source.Respond = _ => throw new InvalidOperationException("gone");
        _source.UsageCommandText = "Unknown command: /usage";
        _poller.UseUsageCommandFallback = true;

        _poller.Start();

        Assert.Equal(1, _source.UsageCommandCalls);
        Assert.False(_poller.ModelLimitsAvailable);
        Assert.Null(_poller.Current);
    }

    [Fact]
    public async Task Polls_never_overlap()
    {
        var slow = new TaskCompletionSource<JsonObject>();
        _source.Respond = call => call == 2 ? slow.Task : Task.FromResult(UsageFixtures.GetUsage());
        _poller.Start();

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, _source.Calls);
        _time.Advance(TimeSpan.FromMinutes(6));
        _poller.NotifyTurnCompleted();
        _poller.NotifyTurnCompleted();
        Assert.Equal(2, _source.Calls);

        slow.SetResult(UsageFixtures.GetUsage());
        await _poller.PollCompletion.WaitAsync(TestContext.Current.CancellationToken);

        // The poll that fell due meanwhile runs once the slow one is done, and only one.
        Assert.Equal(3, _source.Calls);
        Assert.Equal(1, _source.MaxInFlight);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(3, _source.Calls);
    }

    [Fact]
    public async Task Disposing_stops_the_polling()
    {
        _poller.Start();

        await _poller.DisposeAsync();
        _time.Advance(TimeSpan.FromMinutes(20));
        _poller.NotifyTurnCompleted();
        _poller.NotifyRateLimitEvent(UsageFixtures.RateLimitInfo());

        Assert.Equal(1, _source.Calls);
        Assert.Single(_updates);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_the_polling()
    {
        _poller.Updated += _ => throw new InvalidOperationException("handler bug");

        _poller.Start();
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(2, _source.Calls);
    }

    private sealed class FakeUsageSource
    {
        private int _calls;
        private int _inFlight;
        private int _maxInFlight;
        private int _usageCommandCalls;

        public Func<int, Task<JsonObject>> Respond { get; set; } = _ => Task.FromResult(UsageFixtures.GetUsage());

        public string? UsageCommandText { get; set; } = UsageFixtures.UsageCommand();

        public int Calls => Volatile.Read(ref _calls);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int UsageCommandCalls => Volatile.Read(ref _usageCommandCalls);

        public async Task<JsonObject> GetUsageAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var inFlight = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, inFlight);
            try
            {
                return await Respond(call);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<string?> UsageCommandAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _usageCommandCalls);
            return Task.FromResult(UsageCommandText);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
