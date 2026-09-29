using Claudette.Platform.Processes;
using Claudette.Platform.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Platform.Tests.Processes;

public sealed class ProcessSamplerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeProcessTree _tree = new();
    private readonly ProcessSampler _sampler;
    private readonly List<IReadOnlyList<ProcessSnapshot>> _sampled = [];

    public ProcessSamplerTests()
    {
        _sampler = new ProcessSampler(_tree, _time);
        _sampler.Sampled += s =>
        {
            lock (_sampled)
            {
                _sampled.Add(s);
            }
        };
    }

    public void Dispose() => _sampler.Dispose();

    [Fact]
    public void Nothing_is_sampled_until_started()
    {
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, _tree.Samples);
    }

    [Fact]
    public void Starting_samples_at_once_then_every_interval()
    {
        _sampler.Interval = TimeSpan.FromSeconds(2);

        _sampler.Start();
        Assert.Equal(1, _tree.Samples);

        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(1, _tree.Samples);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, _tree.Samples);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, _tree.Samples);
        Assert.Equal(3, _sampled.Count);
    }

    [Fact]
    public void The_default_interval_is_the_summary_interval()
    {
        Assert.Equal(ProcessSampler.SummaryInterval, _sampler.Interval);
        Assert.Equal(TimeSpan.FromSeconds(10), ProcessSampler.SummaryInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), ProcessSampler.PanelInterval);
    }

    [Fact]
    public void The_samples_are_raised()
    {
        var snapshot = new ProcessSnapshot { Pid = 100, ParentPid = 1, Name = "claude", IsRoot = true };
        _tree.OnSample = _ => [snapshot];

        _sampler.Start();

        Assert.Same(snapshot, Assert.Single(Assert.Single(_sampled)));
    }

    [Fact]
    public void A_shorter_interval_samples_at_once_if_it_has_already_passed()
    {
        _sampler.Start();
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, _tree.Samples);

        _sampler.Interval = ProcessSampler.PanelInterval;

        Assert.Equal(2, _tree.Samples);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, _tree.Samples);
    }

    [Fact]
    public void A_shorter_interval_counts_from_the_last_sample()
    {
        _sampler.Start();
        _time.Advance(TimeSpan.FromSeconds(1));

        _sampler.Interval = ProcessSampler.PanelInterval;
        Assert.Equal(1, _tree.Samples);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _tree.Samples);
    }

    [Fact]
    public void A_longer_interval_pushes_the_next_sample_back()
    {
        _sampler.Interval = ProcessSampler.PanelInterval;
        _sampler.Start();

        _sampler.Interval = ProcessSampler.SummaryInterval;
        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, _tree.Samples);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _tree.Samples);
    }

    [Fact]
    public void The_interval_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _sampler.Interval = TimeSpan.Zero);
    }

    [Fact]
    public void Command_lines_are_requested_only_when_asked_for()
    {
        _sampler.Interval = TimeSpan.FromSeconds(1);
        _sampler.Start();

        _sampler.IncludeCommandLines = true;
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([false, true], _tree.CommandLineRequests);
    }

    [Fact]
    public void Nothing_runs_after_stop()
    {
        _sampler.Start();
        _sampler.Stop();

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, _tree.Samples);
        Assert.False(_sampler.IsRunning);
    }

    [Fact]
    public void Nothing_runs_after_dispose()
    {
        _sampler.Start();
        _sampler.Dispose();

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, _tree.Samples);
        Assert.Throws<ObjectDisposedException>(_sampler.Start);
    }

    [Fact]
    public void It_can_be_started_again()
    {
        _sampler.Start();
        _sampler.Stop();
        _time.Advance(TimeSpan.FromMinutes(1));

        _sampler.Start();

        Assert.Equal(2, _tree.Samples);
        _time.Advance(_sampler.Interval);
        Assert.Equal(3, _tree.Samples);
    }

    [Fact]
    public void Starting_twice_does_nothing_more()
    {
        _sampler.Start();
        _sampler.Start();

        Assert.Equal(1, _tree.Samples);
    }

    [Fact]
    public void A_handler_can_stop_the_sampler()
    {
        _sampler.Sampled += _ => _sampler.Stop();

        _sampler.Start();
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, _tree.Samples);
    }

    [Fact]
    public void A_failed_sample_is_skipped_and_sampling_goes_on()
    {
        _tree.OnSample = _ => throw new InvalidOperationException("boom");
        _sampler.Start();
        Assert.Empty(_sampled);

        _tree.OnSample = _ => [];
        _time.Advance(_sampler.Interval);

        Assert.Single(_sampled);
    }

    [Fact]
    public async Task Samples_never_overlap()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        _sampler.Interval = TimeSpan.FromSeconds(1);
        _tree.OnSample = _ =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(5), cancellation);
            return [];
        };

        // The first sample blocks on another thread while the clock runs on.
        var start = Task.Run(_sampler.Start, cancellation);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), cancellation));
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _tree.Samples);

        gate.Set();
        await start;
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, _tree.Samples);
        Assert.Equal(1, _tree.MaxConcurrentSamples);
    }

    [Fact]
    public async Task A_sample_under_way_when_stopped_is_dropped()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        _tree.OnSample = _ =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(5), cancellation);
            return [];
        };

        var start = Task.Run(_sampler.Start, cancellation);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), cancellation));
        _sampler.Stop();
        gate.Set();
        await start;

        Assert.Empty(_sampled);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, _tree.Samples);
    }
}
