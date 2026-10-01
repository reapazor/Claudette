using System.Runtime.InteropServices;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Development;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Restarting a source build of Claudette into its new build, and back if that fails (DESIGN.md §9, "Working on Claudette").</summary>
public class RestartTests
{
    private static readonly DateTime Running = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Rebuilt = Running.AddMinutes(5);

    /// <summary>The window's side, over the harness's shell.</summary>
    private sealed class ShellHost(ShellViewModel shell) : IRestartHost
    {
        public bool Exited { get; private set; }

        public bool AnyTabWorking => shell.AnyTabWorking;

        public event Action? TabsChanged
        {
            add => shell.TabStatusChanged += value;
            remove => shell.TabStatusChanged -= value;
        }

        public RestartSnapshot Capture() => shell.CaptureForRestart();

        public Task CloseTabsAsync(string message) => shell.CloseTabsForRestartAsync();

        public void Recover(RestartSnapshot snapshot) => shell.Restore(null, snapshot);

        public void Exit() => Exited = true;
    }

    /// <summary>A build output with this platform's native libraries and another platform's.</summary>
    private static string MakeBuild(string root)
    {
        var output = Path.Combine(root, "repo", "src", "Claudette.App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(Path.Combine(output, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        Directory.CreateDirectory(Path.Combine(output, "runtimes", "other-os-x64", "native"));
        File.WriteAllText(Path.Combine(output, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "native.lib"), "here");
        File.WriteAllText(Path.Combine(output, "runtimes", "other-os-x64", "native", "native.lib"), "elsewhere");
        foreach (var name in new[] { "Claudette", "Claudette.exe", "Claudette.dll", "Claudette.deps.json", "Claudette.runtimeconfig.json" })
        {
            File.WriteAllText(Path.Combine(output, name), name);
        }
        Touch(output, Running);
        return output;
    }

    private static void Touch(string output, DateTime stamp)
    {
        foreach (var file in Directory.EnumerateFiles(output))
        {
            File.SetLastWriteTimeUtc(file, stamp);
        }
    }

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>
    /// A pinned tab, selected and running, and an unpinned one with a half-typed message, a one-off suffix and a pasted
    /// image. The unpinned one wouldn't come back on a normal launch.
    /// </summary>
    private static async Task<(TabViewModel Pinned, TabViewModel Unpinned)> OpenTabsAsync(TabTestHarness h)
    {
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, UserName = "pinned" }, new TabState { Folder = h.WorkFolder, UserName = "unpinned" }];
        h.Shell.Restore(null);
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = false;
        var (pinned, unpinned) = (h.Shell.AllTabs.First(), h.Shell.AllTabs.Last());
        await TabTestHarness.Eventually(() => pinned.Status == TabStatus.Idle && pinned.IsSettled, "the selected tab to start");
        unpinned.ComposerText = "half-typed";
        unpinned.AddSuffixCommand.Execute(h.Services.Settings.QuickSuffixes[0]);
        unpinned.AddImage(Png, "Pasted image");
        return (pinned, unpinned);
    }

    /// <summary>A restart service that has seen the new build.</summary>
    private static RestartService NewBuildReady(TabTestHarness h, IRestartHost host, string output)
    {
        var restarts = new RestartService(h.Services, new DevelopmentBuild(output, output, Running), host);
        restarts.Start();
        Touch(output, Rebuilt);
        restarts.CheckNow();
        restarts.CheckNow();
        return restarts;
    }

    [Fact]
    public void The_restart_options_are_read_and_not_passed_on()
    {
        string[] args = ["--folder", "work", LaunchArguments.SourceBuildOption, "bin/Debug", LaunchArguments.RestoreOption, "abc"];

        Assert.Equal(Path.GetFullPath("bin/Debug"), LaunchArguments.SourceBuild(args));
        Assert.Equal("abc", LaunchArguments.RestoreNonce(args));
        Assert.Equal(["--folder", "work"], LaunchArguments.WithoutDevelopmentOptions(args));
        Assert.Null(LaunchArguments.RestoreNonce(["--folder", "work"]));
    }

    [Fact]
    public async Task A_new_build_is_offered_once_it_has_finished()
    {
        await using var h = new TabTestHarness();
        var output = MakeBuild(h.Root);
        using var restarts = new RestartService(h.Services, new DevelopmentBuild(output, output, Running), new ShellHost(h.Shell));
        using var badge = new NewBuildViewModel(restarts);
        restarts.Start();
        Assert.False(badge.HasBadge);

        Touch(output, Rebuilt);
        restarts.CheckNow();
        Assert.Null(restarts.ReadyStamp);
        restarts.CheckNow();

        Assert.Equal(Rebuilt, restarts.ReadyStamp);
        Assert.True(badge.HasBadge);
        Assert.Equal("New build ready", badge.BadgeText);
        Assert.True(badge.CanRestart);
    }

    [Fact]
    public async Task Restarting_hands_every_tab_and_what_was_typed_to_the_new_build()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        var output = MakeBuild(h.Root);
        var (pinned, unpinned) = await OpenTabsAsync(h);
        var host = new ShellHost(h.Shell);
        // The new build says it's up as soon as it starts.
        launcher.OnStart = (spec, _) => RestartHandshake.SignalReady(h.Services.Paths.RestartReadyFile, spec.Arguments[^1]);
        using var restarts = NewBuildReady(h, host, output);

        Assert.True(await restarts.RestartAsync());

        Assert.True(host.Exited);
        var spec = launcher.Started.Single();
        var copy = Assert.Single(Directory.GetDirectories(h.Services.Paths.BuildCopiesDirectory));
        Assert.Contains(new[] { spec.FileName }.Concat(spec.Arguments), a => a.StartsWith(copy, StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(copy, "runtimes", RuntimeInformation.RuntimeIdentifier)));
        Assert.False(Directory.Exists(Path.Combine(copy, "runtimes", "other-os-x64")));
        Assert.Equal([LaunchArguments.SourceBuildOption, output, LaunchArguments.RestoreOption], spec.Arguments.TakeLast(4).SkipLast(1));

        var snapshot = RestartSnapshot.Load(h.Services.Paths.RestartFile, spec.Arguments[^1], h.Time.GetUtcNow());
        Assert.NotNull(snapshot);
        Assert.Equal(["pinned", "unpinned"], snapshot.Tabs.Select(t => t.UserName));
        Assert.Equal(pinned.Id, snapshot.SelectedTabId);
        Assert.Equal([pinned.Id], snapshot.RunningTabIds);
        Assert.Equal("half-typed", snapshot.Drafts[unpinned.Id].Text);
        Assert.Equal([h.Services.Settings.QuickSuffixes[0].Id], snapshot.Drafts[unpinned.Id].SuffixIds);
        var image = Assert.Single(snapshot.Drafts[unpinned.Id].Images!);
        Assert.Equal("Pasted image", image.Name);
        Assert.Equal(Png, image.Data);
        Assert.False(snapshot.Drafts.ContainsKey(pinned.Id));

        // This build has stopped its tabs and leaves the files to the new one.
        Assert.Empty(h.Shell.AllTabs);
        Assert.True(h.Services.SuspendSaving);
        Assert.Equal(2, h.Services.State.Tabs.Count);
    }

    [Fact]
    public async Task A_turn_under_way_is_left_for_Claude_Code_to_carry_on_after_the_restart()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        await TabTestHarness.Eventually(() => tab.IsInTurn && tab.State.SessionId == "s1", "the turn");

        var snapshot = h.Shell.CaptureForRestart();
        await h.Shell.CloseTabsForRestartAsync();

        // It isn't interrupted, so the transcript ends in the turn...
        Assert.Equal([tab.Id], snapshot.InterruptedTabIds);
        Assert.DoesNotContain(h.Transport.Sent, m => m["request"]?["subtype"]?.GetValue<string>() == "interrupt");

        // ...and the session resumes with Claude Code told to carry it on, if it's no older than the snapshot can be.
        h.WriteTranscript("s1", """{"type":"user","timestamp":"2026-09-28T10:00:00Z","uuid":"u1","sessionId":"s1","message":{"role":"user","content":"long job"}}""");
        h.Shell.Restore(null, snapshot);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2, "the tab to start again");
        var restored = h.Shell.AllTabs.Single();
        var environment = h.Factory.Launches[^1].EnvironmentOverrides;
        Assert.Equal("s1", h.Factory.Launches[^1].Resume);
        Assert.Equal("1", environment["CLAUDE_CODE_RESUME_INTERRUPTED_TURN"]);
        Assert.Equal("600000", environment["CLAUDE_CODE_RESUME_INTERRUPTED_TURN_MAX_AGE_MS"]);

        // Claude Code's re-run says why it runs; the conversation says so once.
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        h.Transport.Emit("""{"type":"assistant","resume_reason":"interrupted_turn","message":{"content":[{"type":"text","text":"Picking up again."}]}}""");
        h.Transport.Emit("""{"type":"assistant","resume_reason":"interrupted_turn","message":{"content":[{"type":"text","text":"Done."}]}}""");
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => restored.Items.OfType<TurnSummaryItem>().Any(), "the re-run");
        Assert.Single(restored.Items.OfType<NoteItem>(), n => n.Text == "Carrying on with the turn the restart cut off.");

        // Only that once: the next start is as usual.
        await ((IRemoteControlHost)restored).RestartSessionAsync();
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 3, "the restart");
        Assert.False(h.Factory.Launches[^1].EnvironmentOverrides.ContainsKey("CLAUDE_CODE_RESUME_INTERRUPTED_TURN"));
    }

    [Fact]
    public async Task The_new_build_opens_every_tab_with_what_was_typed()
    {
        await using var h = new TabTestHarness();
        var suffix = h.Services.Settings.QuickSuffixes[0];
        var snapshot = new RestartSnapshot
        {
            Tabs = [new TabState { Id = "a", Folder = h.WorkFolder, IsPinned = true, UserName = "pinned" }, new TabState { Id = "b", Folder = h.WorkFolder, UserName = "unpinned" }],
            SelectedTabId = "a",
            Drafts = { ["b"] = new TabDraft("half-typed", [suffix.Id], [new DraftImage("shot.png", Png)]) },
        };

        h.Shell.Restore(null, snapshot);

        // Unpinned tabs come back although Also restore unpinned tabs is off.
        Assert.Equal(["pinned", "unpinned"], h.Shell.AllTabs.Select(t => t.DisplayName));
        Assert.Equal("a", h.Shell.SelectedTab?.Id);
        var unpinned = h.Shell.AllTabs.Last();
        Assert.Equal("half-typed", unpinned.ComposerText);
        Assert.Equal([suffix.Id], unpinned.Chips.Select(c => c.Suffix.Id));
        var image = Assert.Single(unpinned.Attachments);
        Assert.Equal("shot.png", image.Name);
        Assert.Equal(Png, image.Data);
        Assert.True(unpinned.SendCommand.CanExecute(null));
        Assert.Empty(h.Shell.AllTabs.First().Attachments);
        Assert.Equal(["a", "b"], h.Services.State.Tabs.Select(t => t.Id));
    }

    [Fact]
    public async Task A_new_build_that_stops_as_it_starts_gives_the_tabs_back()
    {
        var launcher = new FakeLauncher
        {
            OnStart = (_, process) =>
            {
                process.WriteError("Unhandled exception. System.InvalidOperationException: the new build is broken");
                process.Exit(134);
            },
        };
        await using var h = new TabTestHarness(launcher: launcher);
        var output = MakeBuild(h.Root);
        await OpenTabsAsync(h);
        var host = new ShellHost(h.Shell);
        using var restarts = NewBuildReady(h, host, output);

        Assert.False(await restarts.RestartAsync());

        Assert.False(host.Exited);
        Assert.Contains("exit code 134", restarts.LastError);
        Assert.Contains("the new build is broken", restarts.LastError);
        Assert.Equal(["pinned", "unpinned"], h.Shell.AllTabs.Select(t => t.DisplayName));
        Assert.Equal("half-typed", h.Shell.AllTabs.Last().ComposerText);
        Assert.Equal(Png, Assert.Single(h.Shell.AllTabs.Last().Attachments).Data);
        Assert.False(h.Services.SuspendSaving);
        Assert.False(File.Exists(h.Services.Paths.RestartFile));
        // Still offered, to try again by hand once it's fixed.
        Assert.Equal(Rebuilt, restarts.ReadyStamp);
    }

    [Fact]
    public async Task A_new_build_that_never_says_it_is_up_is_stopped()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        var output = MakeBuild(h.Root);
        await OpenTabsAsync(h);
        using var restarts = NewBuildReady(h, new ShellHost(h.Shell), output);

        var restarting = restarts.RestartAsync();
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return restarting.IsCompleted;
        }, "the restart to give up");

        Assert.False(await restarting);
        Assert.True(launcher.Processes.Single().Killed);
        Assert.Contains("didn't start within", restarts.LastError);
        Assert.Equal(2, InlineDispatcher.Read(() => h.Shell.AllTabs.Count()));
    }

    [Fact]
    public async Task Restart_when_idle_waits_for_the_working_tab()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        var output = MakeBuild(h.Root);
        var (pinned, _) = await OpenTabsAsync(h);
        var host = new ShellHost(h.Shell);
        launcher.OnStart = (spec, _) => RestartHandshake.SignalReady(h.Services.Paths.RestartReadyFile, spec.Arguments[^1]);
        using var restarts = NewBuildReady(h, host, output);
        using var badge = new NewBuildViewModel(restarts);
        pinned.Status = TabStatus.Working;
        Assert.True(badge.CanRestartWhenIdle);
        Assert.NotNull(badge.WorkingText);

        badge.RestartWhenIdleCommand.Execute(null);
        Assert.Empty(launcher.Started);
        Assert.Equal("Restarting when idle…", badge.BadgeText);

        pinned.Status = TabStatus.Idle;

        await TabTestHarness.Eventually(() => host.Exited, "the restart");
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task Restarting_by_itself_happens_once_no_tab_is_working_and_not_again_after_a_failure()
    {
        var launcher = new FakeLauncher { OnStart = (_, process) => process.Exit(1) };
        await using var h = new TabTestHarness(launcher: launcher);
        var output = MakeBuild(h.Root);
        var (pinned, _) = await OpenTabsAsync(h);
        h.Services.State.RestartOnNewBuild = true;
        pinned.Status = TabStatus.Working;
        using var restarts = NewBuildReady(h, new ShellHost(h.Shell), output);
        Assert.True(restarts.IsWaitingForIdle);
        Assert.Empty(launcher.Started);

        pinned.Status = TabStatus.Idle;
        await TabTestHarness.Eventually(() => restarts.LastError is not null, "the failed restart");
        Assert.Single(launcher.Started);

        // The same build isn't tried again by itself; a newer one is.
        var tab = h.Shell.AllTabs.First();
        tab.Status = TabStatus.Working;
        tab.Status = TabStatus.Idle;
        Assert.Single(launcher.Started);
        Touch(output, Rebuilt.AddMinutes(1));
        restarts.CheckNow();
        restarts.CheckNow();
        await TabTestHarness.Eventually(() => launcher.Started.Count == 2, "the next build");
    }
}
