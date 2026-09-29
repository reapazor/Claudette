using Claudette.Core.Processes;
using Claudette.Platform.Processes;
using Claudette.Platform.Tests.Support;

namespace Claudette.Platform.Tests.Processes;

public sealed class TrackingProcessLauncherTests
{
    private readonly FakeLauncher _inner = new();
    private readonly FakeTracker _tracker = new();
    private readonly TrackingProcessLauncher _launcher;

    public TrackingProcessLauncherTests()
    {
        _launcher = new TrackingProcessLauncher(_inner, _tracker);
    }

    [Fact]
    public void A_process_that_doesnt_ask_isnt_tracked()
    {
        var process = _launcher.Start(new ProcessStartSpec("claude", ["--version"]));

        Assert.Same(_inner.Started.Single(), process);
        Assert.Empty(_tracker.Trees);
    }

    [Fact]
    public void A_process_that_asks_is_tracked_from_the_start()
    {
        var process = _launcher.Start(new ProcessStartSpec("claude", []) { TrackProcessTree = true });

        var tree = Assert.Single(_tracker.Trees).Value;
        Assert.Equal(process.Id, tree.RootPid);
        Assert.Equal(_inner.Started.Single().Id, process.Id);
    }

    [Fact]
    public void The_tracked_process_passes_everything_through()
    {
        var process = _launcher.Start(new ProcessStartSpec("claude", []) { TrackProcessTree = true });
        var inner = _inner.Started.Single();

        process.Kill();

        Assert.Equal(1, inner.KillCalls);
        Assert.Same(inner.StandardOutput, process.StandardOutput);
        Assert.Same(inner.StandardError, process.StandardError);
        Assert.Same(inner.Exited, process.Exited);
    }

    [Fact]
    public async Task Disposing_the_process_releases_the_tree_without_ending_anything_more()
    {
        var process = _launcher.Start(new ProcessStartSpec("claude", []) { TrackProcessTree = true });
        var tree = _tracker.Trees[process.Id];

        await process.DisposeAsync();

        Assert.True(_inner.Started.Single().Disposed);
        Assert.True(tree.IsDisposed);
        Assert.Equal(0, tree.KillAllCalls);
    }

    [Fact]
    public void A_tracking_failure_doesnt_break_the_launch()
    {
        _tracker.Failure = new InvalidOperationException("no job for you");

        var process = _launcher.Start(new ProcessStartSpec("claude", []) { TrackProcessTree = true });

        Assert.Same(_inner.Started.Single(), process);
    }
}

public sealed class ProcessTreeRegistryTests
{
    [Fact]
    public void Tracking_the_same_pid_again_gives_the_same_tree()
    {
        var created = 0;
        var registry = new ProcessTreeRegistry(pid =>
        {
            created++;
            return new FakeProcessTree(pid);
        });

        var first = registry.Track(42);
        var second = registry.Track(42);

        Assert.Same(first, second);
        Assert.Same(first, registry.Find(42));
        Assert.Equal(1, created);
        Assert.Null(registry.Find(43));
    }

    [Fact]
    public void A_disposed_tree_is_forgotten()
    {
        var registry = new ProcessTreeRegistry(pid => new FakeProcessTree(pid));
        var first = registry.Track(42);

        first.Dispose();

        Assert.Null(registry.Find(42));
        var second = registry.Track(42);
        Assert.NotSame(first, second);
        Assert.Same(second, registry.Find(42));
    }

    [Fact]
    public void Disposing_an_old_tree_leaves_its_replacement_alone()
    {
        var registry = new ProcessTreeRegistry(pid => new FakeProcessTree(pid));
        var first = registry.Track(42);
        first.Dispose();
        var second = registry.Track(42);

        first.Dispose();

        Assert.Same(second, registry.Find(42));
    }

    [Fact]
    public void The_current_os_has_a_tracker()
    {
        Assert.NotNull(ProcessTreeTracker.CreateForCurrentOS(new FakeLauncher(), TimeProvider.System));
    }
}
