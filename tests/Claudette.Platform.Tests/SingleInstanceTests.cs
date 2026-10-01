using System.Collections.Concurrent;

namespace Claudette.Platform.Tests;

/// <summary>A second launch hands its arguments to the running Claudette (DESIGN.md §4, "Other ways in").</summary>
public class SingleInstanceTests
{
    [Fact]
    public async Task A_second_launch_hands_its_arguments_to_the_first()
    {
        var scope = Path.Combine(Path.GetTempPath(), $"claudette-instance-{Guid.NewGuid():N}");
        using var first = new SingleInstance(scope);
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => received.TrySetResult(args);

        Assert.False(await first.TryHandOffAsync([], TestContext.Current.CancellationToken));
        first.Listen();

        using var second = new SingleInstance(scope);
        Assert.True(await HandOffAsync(second, ["--folder", "/work/api"]));

        Assert.Equal(["--folder", "/work/api"], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_launch_that_goes_at_once_or_a_handler_that_fails_doesnt_stop_it_listening()
    {
        var scope = Path.Combine(Path.GetTempPath(), $"claudette-instance-{Guid.NewGuid():N}");
        using var first = new SingleInstance(scope);
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => (args is ["boom"] ? throw new InvalidOperationException("boom") : (Action)(() => received.TrySetResult(args)))();
        first.Listen();

        // Connects and goes without a word.
        await using (var client = new System.IO.Pipes.NamedPipeClientStream(".", SingleInstance.PipeName(scope), System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        using (var failing = new SingleInstance(scope))
        {
            Assert.True(await HandOffAsync(failing, ["boom"]));
        }

        using var second = new SingleInstance(scope);
        Assert.True(await HandOffAsync(second, ["--folder", "/work/api"]));
        Assert.Equal(["--folder", "/work/api"], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Separate_data_folders_are_separate_instances()
    {
        using var first = new SingleInstance(Path.Combine(Path.GetTempPath(), $"claudette-a-{Guid.NewGuid():N}"));
        first.Listen();
        using var other = new SingleInstance(Path.Combine(Path.GetTempPath(), $"claudette-b-{Guid.NewGuid():N}"));

        Assert.False(await other.TryHandOffAsync(["--folder", "/x"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_instance_that_stops_listening_lets_another_take_over_and_can_listen_again()
    {
        var scope = Path.Combine(Path.GetTempPath(), $"claudette-restart-{Guid.NewGuid():N}");
        using var first = new SingleInstance(scope);
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ArgumentsReceived += args => received.TrySetResult(args);
        first.Listen();
        Assert.True(await HandOffAsync(new SingleInstance(scope), ["--folder", "/work/one"]));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Restarting into a new build (DESIGN.md §9): the new build finds nobody listening, and the claim free.
        first.StopListening();
        Assert.False(first.IsClaimed);
        var newBuild = new SingleInstance(scope);
        Assert.False(await newBuild.TryHandOffAsync([], TestContext.Current.CancellationToken));
        Assert.True(await newBuild.StartAsync([], takeOver: true, TestContext.Current.CancellationToken));
        Assert.True(newBuild.IsClaimed);

        // It didn't start after all: the old build takes launches, and the claim, again.
        newBuild.Dispose();
        received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Listen();
        Assert.True(first.IsClaimed);
        Assert.True(await HandOffAsync(new SingleInstance(scope), ["--folder", "/work/two"]));
        Assert.Equal(["--folder", "/work/two"], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Launches_at_the_same_moment_start_one_Claudette()
    {
        var scope = NewScope();
        var instances = Enumerable.Range(0, 4).Select(_ => new SingleInstance(scope)).ToArray();
        var received = new ConcurrentQueue<IReadOnlyList<string>>();
        foreach (var instance in instances)
        {
            instance.ArgumentsReceived += received.Enqueue;
        }
        try
        {
            var started = await Task.WhenAll(instances.Select((instance, n) =>
                Task.Run(() => instance.StartAsync(["--folder", $"/work/{n}"], cancellationToken: TestContext.Current.CancellationToken))));

            var running = Assert.Single(Enumerable.Range(0, 4), n => started[n]);
            Assert.True(instances[running].IsClaimed);
            await Waiting.UntilAsync(() => received.Count == 3, "the other launches' arguments");
            Assert.Equal(
                Enumerable.Range(0, 4).Where(n => n != running).Select(n => $"/work/{n}").Order(),
                received.Select(args => args[1]).Order());
        }
        finally
        {
            foreach (var instance in instances)
            {
                instance.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_restarted_build_waits_for_the_claim_and_hands_nothing_to_the_old_one()
    {
        var scope = NewScope();
        using var old = new SingleInstance(scope);
        var handedToOld = false;
        old.ArgumentsReceived += _ => handedToOld = true;
        Assert.True(await old.StartAsync([], cancellationToken: TestContext.Current.CancellationToken));

        using var newBuild = new SingleInstance(scope);
        var starting = newBuild.StartAsync(["--restore", "nonce"], takeOver: true, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(starting.IsCompleted);

        old.StopListening();

        Assert.True(await starting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(newBuild.IsClaimed);
        Assert.False(handedToOld);
    }

    [Fact]
    public async Task A_claim_nobody_answers_for_doesnt_stop_Claudette_starting()
    {
        var scope = NewScope();
        Directory.CreateDirectory(scope);
        // Held, as by a Claudette that hung before it listened.
        await using var held = new FileStream(Path.Combine(scope, SingleInstance.ClaimFileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        using var instance = new SingleInstance(scope, patience: TimeSpan.FromMilliseconds(300));

        Assert.True(await instance.StartAsync([], cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(instance.IsClaimed);
    }

    [Fact]
    public async Task Closing_lets_go_of_the_claim()
    {
        var scope = NewScope();
        using (var first = new SingleInstance(scope))
        {
            Assert.True(await first.StartAsync([], cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(first.IsClaimed);
        }

        using var next = new SingleInstance(scope, patience: TimeSpan.Zero);
        Assert.True(await next.StartAsync([], cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(next.IsClaimed);
    }

    [Fact]
    public void Pipe_names_are_short_and_stable()
    {
        var name = SingleInstance.PipeName("/home/me/.local/share/claudette");

        Assert.Equal(name, SingleInstance.PipeName("/home/me/.local/share/claudette"));
        Assert.NotEqual(name, SingleInstance.PipeName("/tmp/dev-home"));
        Assert.True(name.Length <= 32);
    }

    private static string NewScope() => Path.Combine(Path.GetTempPath(), $"claudette-instance-{Guid.NewGuid():N}");

    /// <summary>The listener starts in the background; give it a moment to be ready.</summary>
    private static async Task<bool> HandOffAsync(SingleInstance instance, string[] args)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await instance.TryHandOffAsync(args, TestContext.Current.CancellationToken))
            {
                return true;
            }
        }
        return false;
    }
}
