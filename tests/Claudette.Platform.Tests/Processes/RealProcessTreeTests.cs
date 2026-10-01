using System.Diagnostics;
using System.Runtime.Versioning;
using Claudette.Core.Processes;
using Claudette.Platform.Processes;
using Claudette.Platform.Processes.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Tests.Processes;

/// <summary>
/// Real process trees on the current OS (DESIGN.md §4, Process monitor). Waits here are real time, but only for OS
/// processes to appear or exit, polled with a short limit. They run on their own (<see cref="RealProcesses"/>): their
/// CPU measurements and process scans would see other tests' processes and load.
/// </summary>
[Collection(nameof(RealProcesses))]
public sealed class RealProcessTreeTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly IProcessTreeTracker _tracker;
    private readonly TrackingProcessLauncher _launcher;

    public RealProcessTreeTests()
    {
        _tracker = ProcessTreeTracker.CreateForCurrentOS(new ProcessLauncher(), TimeProvider.System);
        _launcher = new TrackingProcessLauncher(new ProcessLauncher(), _tracker);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_tracks_descendants_and_ends_the_whole_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var run = Start(Cmd, "/c", "cmd /c ping -n 30 127.0.0.1 >nul 2>nul");
        var tree = run.Tree;
        var rootPid = run.Process.Id;
        try
        {
            Assert.True(tree.IsJobBacked, "Expected the root to go into a Job Object.");
            var sample = await SampleUntilAsync(tree, s => s.Any(IsPing));
            TestContext.Current.TestOutputHelper?.WriteLine(Describe(sample));

            var root = Assert.Single(sample, s => s.IsRoot);
            Assert.Equal(rootPid, root.Pid);
            Assert.Equal("cmd.exe", root.Name, ignoreCase: true);
            Assert.EndsWith(@"\cmd.exe", root.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ping -n 30", root.CommandLine, StringComparison.Ordinal);

            var ping = Assert.Single(sample, IsPing);
            var shell = Assert.Single(sample, s => s.Pid == ping.ParentPid);
            Assert.Equal("cmd.exe", shell.Name, ignoreCase: true);
            Assert.Equal(root.Pid, shell.ParentPid);
            Assert.Contains("127.0.0.1", ping.CommandLine, StringComparison.Ordinal);
            Assert.All(sample, s => Assert.True(s.MemoryBytes > 0, $"{s.Name} has no memory figure."));
            Assert.All(sample, s => Assert.NotNull(s.StartTime));
            Assert.All(sample, s => Assert.False(s.IsDetached));
            // Windows gives cmd a console host; it isn't something the tab started.
            Assert.DoesNotContain(sample, s => s.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(new[] { shell.Pid, ping.Pid }.Order(), tree.DescendantIds().Order());
            Assert.Equal(2, ProcessSummary.From(sample).Count);

            await Task.Delay(100, TestContext.Current.CancellationToken);
            var second = tree.Sample(includeCommandLines: false);
            Assert.All(second.Where(s => sample.Any(p => p.Pid == s.Pid)), s => Assert.NotNull(s.CpuPercent));
            Assert.All(second, s => Assert.Null(s.CommandLine));

            await AssertKillAllEndsEverythingAsync(tree, sample, run.Process);
        }
        finally
        {
            await run.DisposeAsync();
        }

        Assert.True(tree.IsDisposed);
        Assert.Null(_tracker.Find(rootPid));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_keeps_tracking_a_process_whose_parent_exited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        // The inner cmd starts a background ping and exits at once, leaving that ping without a parent.
        await using var run = Start(Cmd, "/c", "cmd /c start /b ping -n 30 127.0.0.1 >nul 2>nul & ping -n 31 127.0.0.1 >nul 2>nul");
        var tree = run.Tree;

        var sample = await SampleUntilAsync(tree, s =>
            s.Any(p => IsPing(p) && p.CommandLine!.Contains("-n 30", StringComparison.Ordinal) && p.IsDetached)
            && s.Any(p => IsPing(p) && p.CommandLine!.Contains("-n 31", StringComparison.Ordinal)));

        var orphan = Assert.Single(sample, p => IsPing(p) && p.CommandLine!.Contains("-n 30", StringComparison.Ordinal));
        var attached = Assert.Single(sample, p => IsPing(p) && p.CommandLine!.Contains("-n 31", StringComparison.Ordinal));
        Assert.DoesNotContain(sample, p => p.Pid == orphan.ParentPid);
        Assert.False(attached.IsDetached);
        Assert.Equal(run.Process.Id, attached.ParentPid);
        Assert.Contains(orphan.Pid, tree.DescendantIds());

        await AssertKillAllEndsEverythingAsync(tree, sample, run.Process);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_without_a_job_walks_parent_pids()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await using var process = new ProcessLauncher().Start(new ProcessStartSpec(Cmd, ["/c", "cmd /c ping -n 30 127.0.0.1 >nul 2>nul"]));
        using var tree = WindowsProcessTree.Create(process.Id, TimeProvider.System, NullLogger.Instance, useJob: false);
        try
        {
            Assert.False(tree.IsJobBacked);
            var sample = await SampleUntilAsync(tree, s => s.Any(IsPing));

            var ping = Assert.Single(sample, IsPing);
            Assert.Contains(ping.Pid, tree.DescendantIds());
            Assert.True(Assert.Single(sample, s => s.IsRoot).Pid == process.Id);

            await AssertKillAllEndsEverythingAsync(tree, sample, process);
        }
        finally
        {
            tree.KillAll();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_stops_a_console_process_without_waiting_out_the_grace_period()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await using var run = Start(Cmd, "/c", "cmd /c ping -n 30 127.0.0.1 >nul 2>nul");
        var ping = Assert.Single(await SampleUntilAsync(run.Tree, s => s.Any(IsPing)), IsPing);
        using var pingProcess = Process.GetProcessById(ping.Pid);

        var stopwatch = Stopwatch.StartNew();
        await run.Tree.StopAsync(ping.Pid, TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

        Assert.True(pingProcess.WaitForExit(WaitLimit), "ping is still running.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Stopping took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Stopping_a_process_outside_the_tree_does_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "Windows and Linux only.");
        await using var run = OperatingSystem.IsWindows()
            ? Start(Cmd, "/c", "ping -n 30 127.0.0.1 >nul 2>nul")
            : Start("/bin/sh", "-c", "sleep 30");

        await run.Tree.StopAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Environment.ProcessId, run.Tree.DescendantIds());
        Assert.False(run.Process.Exited.IsCompleted);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_tracks_descendants_and_ends_the_whole_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var run = Start("/bin/sh", "-c", "sleep 30 & sleep 31 & wait");
        var tree = run.Tree;

        var sample = await SampleUntilAsync(tree, s => s.Count(p => p.Name == "sleep") == 2);

        var root = Assert.Single(sample, s => s.IsRoot);
        Assert.Equal(run.Process.Id, root.Pid);
        var sleeps = sample.Where(s => s.Name == "sleep").OrderBy(s => s.CommandLine).ToList();
        Assert.Equal(["sleep 30", "sleep 31"], sleeps.Select(s => s.CommandLine));
        Assert.All(sleeps, s => Assert.Equal(root.Pid, s.ParentPid));
        Assert.All(sleeps, s => Assert.False(s.IsDetached));
        Assert.All(sample, s => Assert.True(s.MemoryBytes > 0, $"{s.Name} has no memory figure."));
        Assert.All(sample, s => Assert.NotNull(s.StartTime));
        Assert.Equal(sleeps.Select(s => s.Pid).Order(), tree.DescendantIds().Order());

        await Task.Delay(100, TestContext.Current.CancellationToken);
        var second = tree.Sample(includeCommandLines: false);
        Assert.All(second, s => Assert.NotNull(s.CpuPercent));
        Assert.All(second, s => Assert.Null(s.CommandLine));

        await AssertKillAllEndsEverythingAsync(tree, sample, run.Process);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_reads_names_executables_start_times_and_memory()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var run = Start("/bin/sh", "-c", "sleep 30 & wait");

        var sample = await SampleUntilAsync(run.Tree, s => s.Any(p => p.Name == "sleep"));

        var sleep = Assert.Single(sample, p => p.Name == "sleep");
        Assert.Equal("sleep", Path.GetFileName(sleep.ExecutablePath));
        Assert.Equal("sleep 30", sleep.CommandLine);
        // Start times come from the boot time and clock ticks in /proc; a wrong unit would be far off.
        Assert.All(sample, p => Assert.InRange(DateTimeOffset.UtcNow - p.StartTime!.Value, TimeSpan.FromSeconds(-5), TimeSpan.FromMinutes(1)));
        Assert.All(sample, p => Assert.InRange(p.MemoryBytes, 64 * 1024, 1024L * 1024 * 1024));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_keeps_listing_a_process_whose_parent_exited()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        // The inner shell starts a background sleep and exits a second later, leaving that sleep to be re-parented.
        await using var run = Start("/bin/sh", "-c", "sh -c 'sleep 30 & sleep 1' & sleep 31");
        var tree = run.Tree;
        var first = await SampleUntilAsync(tree, s => s.Any(p => p.CommandLine == "sleep 30") && s.Any(p => p.CommandLine == "sleep 31"));
        var orphanPid = Assert.Single(first, p => p.CommandLine == "sleep 30").Pid;

        var sample = await SampleUntilAsync(tree, s => s.Any(p => p.Pid == orphanPid && p.IsDetached));

        var orphan = Assert.Single(sample, p => p.Pid == orphanPid);
        Assert.DoesNotContain(sample, p => p.Pid == orphan.ParentPid);
        var attached = Assert.Single(sample, p => p.CommandLine == "sleep 31");
        Assert.False(attached.IsDetached);
        Assert.Equal(run.Process.Id, attached.ParentPid);
        Assert.Contains(orphanPid, tree.DescendantIds());

        await AssertKillAllEndsEverythingAsync(tree, sample, run.Process);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_stops_a_process_with_SIGTERM_and_one_that_ignores_it_with_SIGKILL()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var run = Start("/bin/sh", "-c", "sleep 30 & sh -c 'trap \"\" TERM; while :; do sleep 1; done' & wait");
        var sample = await SampleUntilAsync(run.Tree, s => s.Any(p => p.Name == "sleep" && p.CommandLine == "sleep 30") && s.Any(p => p.Name == "sh" && !p.IsRoot));
        var polite = Assert.Single(sample, p => p.CommandLine == "sleep 30");
        var stubborn = Assert.Single(sample, p => p.Name == "sh" && !p.IsRoot);
        var grace = TimeSpan.FromMilliseconds(700);

        var stopwatch = Stopwatch.StartNew();
        await run.Tree.StopAsync(polite.Pid, grace, TestContext.Current.CancellationToken);
        Assert.True(stopwatch.Elapsed < grace, $"sleep took {stopwatch.Elapsed} to stop: SIGTERM should have been enough.");
        Assert.DoesNotContain(polite.Pid, run.Tree.DescendantIds());

        stopwatch.Restart();
        await run.Tree.StopAsync(stubborn.Pid, grace, TestContext.Current.CancellationToken);
        Assert.True(stopwatch.Elapsed >= grace, "The shell ignoring SIGTERM stopped before the grace period ended.");
        Assert.DoesNotContain(stubborn.Pid, run.Tree.DescendantIds());
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_measures_the_CPU_a_busy_process_uses()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var run = Start("/bin/sh", "-c", "sh -c 'while :; do :; done' & sleep 30 & wait");
        await SampleUntilAsync(run.Tree, s => s.Any(p => p.Name == "sleep") && s.Any(p => p.Name == "sh" && !p.IsRoot));

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        // As the Processes page samples: a fresh scan. A summary may use a scan another tab made in the last few seconds.
        var sample = run.Tree.Sample(includeCommandLines: true);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("; ", sample.Select(p => $"{p.Name} {p.CpuPercent:0.#}%")));

        // 100% is one core, as in top. A loop that never sleeps gets most of one even on a busy machine.
        Assert.InRange(Assert.Single(sample, p => p.Name == "sh" && !p.IsRoot).CpuPercent!.Value, 20, 105);
        Assert.InRange(Assert.Single(sample, p => p.Name == "sleep").CpuPercent!.Value, 0, 5);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_ends_a_tree_that_keeps_starting_processes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        // Every sleep this tree starts carries a marker, so any left behind can be found afterwards.
        var marker = $"30.{Random.Shared.Next(100000, 999999)}";
        await using var run = Start("/bin/sh", "-c", $"while :; do sleep {marker} & sleep 0.02; done");
        var sample = await SampleUntilAsync(run.Tree, s => s.Count(p => p.CommandLine == $"sleep {marker}") >= 5);

        run.Tree.KillAll();

        await run.Process.Exited.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        var leftOver = await WaitUntilAsync(() => LiveProcessesWith(marker), survivors => survivors.Count == 0);
        Assert.True(leftOver.Count == 0, $"Still running after KillAll: {string.Join(", ", leftOver)}");
    }

    /// <summary>Live processes whose command line mentions <paramref name="marker"/>, from /proc; zombies aren't counted.</summary>
    [SupportedOSPlatform("linux")]
    private static List<int> LiveProcessesWith(string marker)
    {
        var found = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }
            try
            {
                var commandLine = File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ');
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var state = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0];
                if (commandLine.Contains(marker, StringComparison.Ordinal) && state != "Z")
                {
                    found.Add(pid);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone meanwhile.
            }
        }
        return found;
    }

    private static async Task<T> WaitUntilAsync<T>(Func<T> read, Func<T, bool> done)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = read();
        while (!done(value) && stopwatch.Elapsed < WaitLimit)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            value = read();
        }
        return value;
    }

    private TrackedRun Start(string fileName, params string[] arguments)
    {
        var process = _launcher.Start(new ProcessStartSpec(fileName, arguments) { TrackProcessTree = true });
        var tree = _tracker.Find(process.Id);
        if (tree is null)
        {
            process.Kill();
            Assert.Fail("The process wasn't tracked.");
        }
        return new TrackedRun(process, tree);
    }

    private static bool IsPing(ProcessSnapshot snapshot) => snapshot.Name.Equals("ping.exe", StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<ProcessSnapshot>> SampleUntilAsync(ProcessTree tree, Func<IReadOnlyList<ProcessSnapshot>, bool> done)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var sample = tree.Sample(includeCommandLines: true);
            if (done(sample))
            {
                return sample;
            }
            if (stopwatch.Elapsed > WaitLimit)
            {
                Assert.Fail("The expected processes didn't appear. Saw: " + Describe(sample));
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static string Describe(IReadOnlyList<ProcessSnapshot> sample) =>
        string.Join("; ", sample.Select(s =>
            $"{s.Pid}<{s.ParentPid} {s.Name} {s.MemoryBytes / 1024} KB [{s.CommandLine}]{(s.IsRoot ? " root" : "")}{(s.IsDetached ? " detached" : "")}"));

    private static async Task AssertKillAllEndsEverythingAsync(ProcessTree tree, IReadOnlyList<ProcessSnapshot> sample, IRunningProcess root)
    {
        var processes = new List<Process>();
        foreach (var snapshot in sample)
        {
            try
            {
                processes.Add(Process.GetProcessById(snapshot.Pid));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        try
        {
            tree.KillAll();

            foreach (var process in processes)
            {
                Assert.True(process.WaitForExit(WaitLimit), $"PID {process.Id} is still running.");
            }
            await root.Exited.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            Assert.Empty(tree.DescendantIds());
            Assert.Empty(tree.Sample(includeCommandLines: false));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>A tracked process that is always cleaned up: its whole tree is ended, then it's disposed.</summary>
    private sealed class TrackedRun(IRunningProcess process, ProcessTree tree) : IAsyncDisposable
    {
        public IRunningProcess Process => process;

        public ProcessTree Tree => tree;

        public async ValueTask DisposeAsync()
        {
            tree.KillAll();
            await process.DisposeAsync();
        }
    }
}

/// <summary>Tests that measure real processes, run apart from every other test.</summary>
[CollectionDefinition(nameof(RealProcesses), DisableParallelization = true)]
public sealed class RealProcesses;
