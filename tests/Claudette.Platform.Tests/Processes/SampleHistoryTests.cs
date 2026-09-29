using Claudette.Platform.Processes;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Platform.Tests.Processes;

public sealed class SampleHistoryTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Cpu_is_unknown_on_the_first_sample()
    {
        var history = new SampleHistory(_time, cpuDivisor: 1);

        var snapshot = Assert.Single(history.Update([Raw(10, cpuSeconds: 5)]));

        Assert.Null(snapshot.CpuPercent);
        Assert.Equal(_time.GetUtcNow(), snapshot.FirstSeen);
    }

    [Fact]
    public void Cpu_is_the_cpu_time_used_over_the_wall_clock_time()
    {
        var history = new SampleHistory(_time, cpuDivisor: 1);
        history.Update([Raw(10, cpuSeconds: 5)]);

        _time.Advance(TimeSpan.FromSeconds(2));
        var snapshot = Assert.Single(history.Update([Raw(10, cpuSeconds: 6)]));

        Assert.Equal(50, snapshot.CpuPercent!.Value, precision: 6);
    }

    [Fact]
    public void Cpu_can_pass_100_percent_when_100_percent_is_one_core()
    {
        var history = new SampleHistory(_time, cpuDivisor: 1);
        history.Update([Raw(10, cpuSeconds: 0)]);

        _time.Advance(TimeSpan.FromSeconds(1));
        var snapshot = Assert.Single(history.Update([Raw(10, cpuSeconds: 3)]));

        Assert.Equal(300, snapshot.CpuPercent!.Value, precision: 6);
    }

    [Fact]
    public void Cpu_is_divided_across_cores_when_100_percent_is_all_cores()
    {
        var history = new SampleHistory(_time, cpuDivisor: 8);
        history.Update([Raw(10, cpuSeconds: 0)]);

        _time.Advance(TimeSpan.FromSeconds(2));
        var snapshot = Assert.Single(history.Update([Raw(10, cpuSeconds: 4)]));

        Assert.Equal(25, snapshot.CpuPercent!.Value, precision: 6);
    }

    [Fact]
    public void First_seen_is_kept_across_samples()
    {
        var history = new SampleHistory(_time, cpuDivisor: 1);
        var first = _time.GetUtcNow();
        history.Update([Raw(10)]);

        _time.Advance(TimeSpan.FromSeconds(10));
        var snapshots = history.Update([Raw(10), Raw(11)]);

        Assert.Equal(first, snapshots[0].FirstSeen);
        Assert.Equal(first.AddSeconds(10), snapshots[1].FirstSeen);
        Assert.Null(snapshots[1].CpuPercent);
    }

    [Fact]
    public void A_reused_pid_is_a_new_process()
    {
        var history = new SampleHistory(_time, cpuDivisor: 1);
        history.Update([Raw(10, startKey: 1, cpuSeconds: 100)]);

        _time.Advance(TimeSpan.FromSeconds(2));
        var snapshot = Assert.Single(history.Update([Raw(10, startKey: 2, cpuSeconds: 1)]));

        Assert.Null(snapshot.CpuPercent);
        Assert.Equal(_time.GetUtcNow(), snapshot.FirstSeen);
    }

    [Fact]
    public void No_time_passing_keeps_the_previous_figure()
    {
        var history = new SampleHistory(_time, cpuDivisor: 1);
        history.Update([Raw(10, cpuSeconds: 0)]);
        _time.Advance(TimeSpan.FromSeconds(1));
        history.Update([Raw(10, cpuSeconds: 0.5)]);

        var snapshot = Assert.Single(history.Update([Raw(10, cpuSeconds: 0.5)]));

        Assert.Equal(50, snapshot.CpuPercent!.Value, precision: 6);
    }

    [Fact]
    public void Details_are_carried_through()
    {
        var history = new SampleHistory(_time, cpuDivisor: 1);
        var start = new DateTimeOffset(2026, 9, 28, 11, 0, 0, TimeSpan.Zero);

        var snapshot = Assert.Single(history.Update([
            new RawProcess(10, 9, 1, "node", "/usr/bin/node", "node server.js", TimeSpan.Zero, 1234, start, IsRoot: true, IsDetached: true),
        ]));

        Assert.Equal(
            new ProcessSnapshot
            {
                Pid = 10,
                ParentPid = 9,
                Name = "node",
                ExecutablePath = "/usr/bin/node",
                CommandLine = "node server.js",
                MemoryBytes = 1234,
                StartTime = start,
                IsRoot = true,
                IsDetached = true,
                FirstSeen = _time.GetUtcNow(),
            },
            snapshot);
    }

    [Theory]
    [InlineData(1.0, 1.0, 1, 100.0)]
    [InlineData(0.5, 2.0, 1, 25.0)]
    [InlineData(4.0, 1.0, 4, 100.0)]
    [InlineData(0.0, 1.0, 1, 0.0)]
    [InlineData(1.0, 0.0, 1, 0.0)]
    [InlineData(-1.0, 1.0, 1, 0.0)]
    public void Cpu_percent_math(double cpuSeconds, double wallSeconds, int divisor, double expected)
    {
        Assert.Equal(expected, SampleHistory.CpuPercent(TimeSpan.FromSeconds(cpuSeconds), TimeSpan.FromSeconds(wallSeconds), divisor), precision: 6);
    }

    private static RawProcess Raw(int pid, long startKey = 1, double cpuSeconds = 0) =>
        new(pid, 1, startKey, "p", null, null, TimeSpan.FromSeconds(cpuSeconds), 0, null, IsRoot: false, IsDetached: false);
}
