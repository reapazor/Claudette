using System.Runtime.Versioning;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;
using Claudette.Core.RemoteControl;
using Claudette.Platform.Power;
using Claudette.Platform.Power.Windows;
using Claudette.Platform.Tests.Support;

namespace Claudette.Platform.Tests.Power;

/// <summary>
/// Keeping the computer awake while tabs are connected to the Claude app (DESIGN.md §18, "Remote Control"). The helpers
/// run through a fake launcher; the Windows call and <c>caffeinate</c> run for real only on their own OS, and the process
/// mechanics with a harmless <c>sleep</c> on macOS and Linux.
/// </summary>
public class SleepBlockerTests
{
    private readonly FakeLauncher _launcher = new();

    [Fact]
    public async Task Caffeinate_holds_idle_sleep_off_until_its_ended_or_Claudette_exits()
    {
        var blocker = ProcessSleepBlocker.Caffeinate(_launcher, 4321);

        blocker.SetBlocking(true);
        blocker.SetBlocking(true);

        var spec = Assert.Single(_launcher.Specs);
        Assert.Equal("/usr/bin/caffeinate", spec.FileName);
        Assert.Equal(["-i", "-w", "4321"], spec.Arguments);
        Assert.True(blocker.IsBlocking);
        Assert.Contains("caffeinate", blocker.Describe(), StringComparison.Ordinal);

        blocker.SetBlocking(false);

        Assert.False(blocker.IsBlocking);
        Assert.Equal(1, _launcher.Started[0].KillCalls);
        _launcher.Started[0].Exit(0);
        await Eventually(() => _launcher.Started[0].Disposed);
        // Nothing to report: it was ended on purpose.
        Assert.Equal("Not keeping the computer awake now.", blocker.Describe());
    }

    [Fact]
    public void Systemd_inhibit_blocks_sleep_while_claudette_runs()
    {
        var blocker = ProcessSleepBlocker.SystemdInhibit(_launcher, 4242, new Probe("/usr/bin/systemd-inhibit"));

        blocker.SetBlocking(true);

        var spec = Assert.Single(_launcher.Specs);
        Assert.Equal("/usr/bin/systemd-inhibit", spec.FileName);
        Assert.Equal(
            ["--what=sleep", "--who=Claudette", "--why=Tabs are connected to the Claude app", "--mode=block",
             "/bin/sh", "-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 10; done", "claudette-awake", "4242"],
            spec.Arguments);
        // Claudette's own environment, like its other helpers.
        Assert.Null(spec.Environment);
        blocker.Dispose();
        Assert.False(blocker.IsBlocking);
        Assert.Equal(1, _launcher.Started[0].KillCalls);
    }

    [Fact]
    public void Without_systemd_inhibit_nothing_is_kept_awake_and_Diagnostics_says_why()
    {
        var blocker = ProcessSleepBlocker.SystemdInhibit(_launcher, 4242, new Probe(null));

        blocker.SetBlocking(true);

        Assert.Empty(_launcher.Specs);
        Assert.False(blocker.IsBlocking);
        Assert.Equal(ProcessSleepBlocker.SystemdInhibitMissing, blocker.UnavailableReason);
        Assert.Equal($"Not available: {ProcessSleepBlocker.SystemdInhibitMissing}", blocker.Describe());
    }

    [Fact]
    public async Task A_helper_that_stops_by_itself_no_longer_blocks_and_says_why()
    {
        var blocker = ProcessSleepBlocker.SystemdInhibit(_launcher, 4242, new Probe("/usr/bin/systemd-inhibit"));
        blocker.SetBlocking(true);

        _launcher.Started[0].WriteError("Failed to inhibit: Access denied");
        _launcher.Started[0].Exit(1);

        await Eventually(() => !blocker.IsBlocking);
        Assert.Equal("Not keeping the computer awake: systemd-inhibit stopped (exit code 1): Failed to inhibit: Access denied", blocker.Describe());

        // Asked again, it tries again.
        blocker.SetBlocking(true);
        Assert.Equal(2, _launcher.Started.Count);
        Assert.True(blocker.IsBlocking);
    }

    [Fact]
    public void Every_OS_gets_a_blocker_or_says_why_not()
    {
        using var blocker = SleepBlockers.CreateForCurrentOS(_launcher);

        Assert.False(blocker.IsBlocking);
        Assert.False(string.IsNullOrEmpty(blocker.Describe()));
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsSleepBlocker>(blocker);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<ProcessSleepBlocker>(blocker);
        }
    }

    [Fact]
    public async Task A_real_helper_runs_until_its_ended()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The helpers run on macOS and Linux.");
        // A harmless stand-in for caffeinate and systemd-inhibit: it blocks nothing.
        var blocker = new ProcessSleepBlocker(new ProcessLauncher(), new ProcessStartSpec("sleep", ["30"]), "sleep");

        blocker.SetBlocking(true);
        await Waiting.NeverAsync(() => !blocker.IsBlocking, "the helper stopped by itself");

        blocker.SetBlocking(false);
        Assert.False(blocker.IsBlocking);

        var failing = new ProcessSleepBlocker(new ProcessLauncher(), new ProcessStartSpec("sh", ["-c", "echo 'no logind here' >&2; exit 3"]), "sh");
        failing.SetBlocking(true);
        await Eventually(() => !failing.IsBlocking);
        Assert.Equal("Not keeping the computer awake: sh stopped (exit code 3): no logind here", failing.Describe());
    }

    [Fact]
    public async Task The_inhibitors_command_ends_when_claudette_does()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The helpers run on macOS and Linux.");
        // The command systemd-inhibit runs, without systemd-inhibit: it waits while the process it watches is there.
        static ProcessStartSpec Watching(int pid) => new("/bin/sh", ProcessSleepBlocker.SystemdInhibitArguments(pid).Skip(5).ToArray());
        var gone = System.Diagnostics.Process.Start("sh", ["-c", "exit 0"])!;
        await gone.WaitForExitAsync(TestContext.Current.CancellationToken);

        await using var watchingGone = new ProcessLauncher().Start(Watching(gone.Id));
        await using var watchingUs = new ProcessLauncher().Start(Watching(Environment.ProcessId));

        Assert.Equal(0, await watchingGone.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(watchingUs.Exited.IsCompleted);
        watchingUs.Kill();
    }

    [Fact]
    public async Task Caffeinate_runs_for_real_on_macOS()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");
        using var blocker = ProcessSleepBlocker.Caffeinate(new ProcessLauncher(), Environment.ProcessId);

        blocker.SetBlocking(true);
        await Waiting.NeverAsync(() => !blocker.IsBlocking, $"caffeinate stopped: {blocker.Describe()}", TimeSpan.FromMilliseconds(300));

        blocker.SetBlocking(false);
        Assert.False(blocker.IsBlocking);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SetThreadExecutionState_is_set_and_cleared_on_Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using var blocker = new WindowsSleepBlocker();

        blocker.SetBlocking(true);
        await Eventually(() => blocker.IsBlocking);
        Assert.StartsWith("Keeping the computer awake", blocker.Describe(), StringComparison.Ordinal);

        blocker.SetBlocking(false);
        await Eventually(() => !blocker.IsBlocking);
    }

    private static Task Eventually(Func<bool> condition) => Waiting.UntilAsync(condition);

    private sealed class Probe(string? systemdInhibit) : IFileProbe
    {
        public bool FileExists(string path) => path == systemdInhibit;

        public string? FindOnPath(string fileName) => fileName == "systemd-inhibit" ? systemdInhibit : null;

        public string ExpandEnvironmentVariables(string path) => path;
    }
}
