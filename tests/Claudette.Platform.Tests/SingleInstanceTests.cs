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

        // Restarting into a new build (DESIGN.md §9): the new build finds nobody listening.
        first.StopListening();
        using var newBuild = new SingleInstance(scope);
        Assert.False(await newBuild.TryHandOffAsync([], TestContext.Current.CancellationToken));

        // It didn't start after all: the old build takes launches again.
        received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Listen();
        Assert.True(await HandOffAsync(new SingleInstance(scope), ["--folder", "/work/two"]));
        Assert.Equal(["--folder", "/work/two"], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Pipe_names_are_short_and_stable()
    {
        var name = SingleInstance.PipeName("/home/me/.local/share/claudette");

        Assert.Equal(name, SingleInstance.PipeName("/home/me/.local/share/claudette"));
        Assert.NotEqual(name, SingleInstance.PipeName("/tmp/dev-home"));
        Assert.True(name.Length <= 32);
    }

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
