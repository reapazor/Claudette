using System.ComponentModel;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.ProjectTools;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// A tab's project runs (DESIGN.md §4, "Sidebar"; §18, "Project tools"): each job makes an entry under the tab's row
/// with its own log, which stays, whatever the result, until the user closes it. The engine is a few files in a
/// temporary folder, and every process is a fake.
/// </summary>
public class ProjectRunsTests
{
    private static async Task<(TabTestHarness H, FakeLauncher Launcher, TabViewModel Tab)> OpenAsync()
    {
        var (h, launcher, _) = ProjectToolsTests.UnrealHarness();
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.ProjectTools.Project is not null, "the project");
        return (h, launcher, tab);
    }

    private static ProjectAction Action(TabViewModel tab, string id) => InlineDispatcher.Read(() => tab.ProjectTools.Actions.Single(a => a.Id == id));

    private static IReadOnlyList<ProjectRunViewModel> Runs(TabViewModel tab) => InlineDispatcher.Read(() => tab.ProjectTools.Runs.Items.ToArray());

    /// <summary>Runs <paramref name="id"/> and ends its process with <paramref name="exitCode"/>.</summary>
    private static async Task<ProjectRunViewModel> RunToEndAsync(TabViewModel tab, FakeLauncher launcher, string id, int exitCode, string? line = null)
    {
        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, id));
        var run = Runs(tab)[^1];
        var process = launcher.Processes.Last();
        if (line is not null)
        {
            process.WriteOutput(line);
        }
        process.Exit(exitCode);
        await TabTestHarness.Eventually(() => !run.IsRunning, $"{run.Name} to end");
        return run;
    }

    [Fact]
    public async Task Each_job_makes_an_entry_that_keeps_its_log_after_it_ends()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        Assert.False(tab.ProjectTools.Runs.HasRuns);
        Assert.False(tab.ProjectTools.Runs.HasSelectedRun);

        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, "build-editor"));

        var build = Assert.Single(Runs(tab));
        Assert.True(tab.ProjectTools.Runs.HasRuns);
        Assert.Same(build, tab.ProjectTools.Runs.RunningRun);
        Assert.Same(build, tab.ProjectTools.Runs.SelectedRun);
        Assert.Equal("Build editor", build.Name);
        Assert.Equal(h.Time.GetUtcNow(), build.Started);
        Assert.True(build.IsRunning);
        Assert.Equal("●", build.Glyph);
        Assert.Equal("Running · 0s", build.Detail);
        Assert.False(build.CloseCommand.CanExecute(null));

        // The running time ticks from the injected clock.
        var ticks = 0;
        build.PropertyChanged += (_, e) => ticks += e.PropertyName == nameof(ProjectRunViewModel.Detail) ? 1 : 0;
        h.Time.Advance(TimeSpan.FromSeconds(65));
        Assert.True(ticks > 0);
        Assert.Equal("Running · 1m 05s", build.Detail);

        launcher.Processes.Last().WriteOutput("Building NightOwlEditor...");
        await TabTestHarness.Eventually(() => build.Output.Contains("Building NightOwlEditor..."), "the output");
        launcher.Processes.Last().Exit(0);
        await TabTestHarness.Eventually(() => build.Succeeded, "the end");

        // It stays, with its log, after it ends.
        Assert.Same(build, Assert.Single(Runs(tab)));
        Assert.Null(tab.ProjectTools.Runs.RunningRun);
        Assert.Equal("✓", build.Glyph);
        Assert.Equal($"Succeeded · {MessageTimes.Short(h.Time.GetUtcNow(), h.Time)}", build.Detail);
        Assert.Equal("Build editor succeeded.", build.Output[^1]);
        Assert.Contains("took 1m 05s", build.Tip, StringComparison.Ordinal);
        Assert.True(build.CloseCommand.CanExecute(null));
        Assert.False(build.StopCommand.CanExecute(null));

        // A second job makes a second entry, which the Project page shows; the first keeps its log.
        var generate = await RunToEndAsync(tab, launcher, "generate-project-files", 6, "Generating...");

        Assert.Equal([build, generate], Runs(tab));
        Assert.Same(generate, tab.ProjectTools.Runs.SelectedRun);
        Assert.Contains("Building NightOwlEditor...", build.Output);
        Assert.DoesNotContain("Building NightOwlEditor...", generate.Output);
        Assert.Contains("Generating...", generate.Output);
        Assert.Equal("✕", generate.Glyph);
        Assert.True(generate.Failed);
        Assert.Equal(6, generate.ExitCode);
        Assert.StartsWith("Failed · exit code 6 · ", generate.Detail, StringComparison.Ordinal);
        Assert.Equal("Generate project files failed (exit code 6).", generate.Status);
    }

    [Fact]
    public async Task Closing_an_entry_takes_it_and_its_log_away_and_the_page_shows_the_newest_left()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        var first = await RunToEndAsync(tab, launcher, "build-editor", 0);
        var second = await RunToEndAsync(tab, launcher, "generate-project-files", 1);
        var third = await RunToEndAsync(tab, launcher, "build-editor", 2);

        // The Project page shows the one clicked in the sidebar.
        first.OpenCommand.Execute(null);
        Assert.Same(first, tab.ProjectTools.Runs.SelectedRun);

        first.CloseCommand.Execute(null);

        Assert.Equal([second, third], Runs(tab));
        Assert.Same(third, tab.ProjectTools.Runs.SelectedRun);

        // Closing one the page isn't showing leaves the page alone.
        second.CloseCommand.Execute(null);
        Assert.Equal([third], Runs(tab));
        Assert.Same(third, tab.ProjectTools.Runs.SelectedRun);

        third.CloseCommand.Execute(null);
        Assert.Empty(Runs(tab));
        Assert.False(tab.ProjectTools.Runs.HasRuns);
        Assert.Null(tab.ProjectTools.Runs.SelectedRun);
        Assert.False(tab.ProjectTools.Runs.HasSelectedRun);
    }

    [Fact]
    public async Task A_running_entry_cant_be_closed_but_can_be_stopped_and_then_closed()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, "build-editor"));
        var run = Runs(tab).Single();
        var process = launcher.Processes.Last();

        Assert.False(run.CloseCommand.CanExecute(null));
        run.CloseCommand.Execute(null);
        Assert.Same(run, Runs(tab).Single());
        Assert.False(process.Killed);

        Assert.True(run.StopCommand.CanExecute(null));
        run.StopCommand.Execute(null);
        await TabTestHarness.Eventually(() => run.IsStopped, "the stop");

        Assert.True(process.Killed);
        Assert.Equal("■", run.Glyph);
        Assert.StartsWith("Stopped · ", run.Detail, StringComparison.Ordinal);
        Assert.Same(run, Runs(tab).Single());
        Assert.True(run.CloseCommand.CanExecute(null));
        run.CloseCommand.Execute(null);
        Assert.Empty(Runs(tab));
    }

    [Fact]
    public async Task A_job_that_cant_start_makes_a_failed_entry_that_says_why()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        launcher.OnStart = (spec, _) =>
        {
            if (spec.TrackProcessTree)
            {
                throw new Win32Exception("No such file or directory");
            }
        };

        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, "build-editor"));

        var run = Runs(tab).Single();
        Assert.Same(run, tab.ProjectTools.Runs.SelectedRun);
        Assert.False(tab.ProjectTools.Runs.IsJobRunning);
        Assert.True(run.Failed);
        Assert.Null(run.ExitCode);
        Assert.Equal("Build editor couldn't start: No such file or directory", run.Status);
        Assert.Equal("Couldn't start it: No such file or directory", run.Output[^1]);
        Assert.StartsWith("$ ", run.Output[0], StringComparison.Ordinal);
        Assert.StartsWith("Couldn't start · ", run.Detail, StringComparison.Ordinal);
        Assert.True(run.CloseCommand.CanExecute(null));
        Assert.Contains(tab.Items, i => i is NoteItem note && note.Text.StartsWith("Couldn't start Build editor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleans_deletion_makes_an_entry_and_Launch_editor_doesnt()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "Intermediate"));
        await tab.ProjectTools.RefreshCommand.ExecuteAsync(null);

        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, "launch-editor"));

        Assert.True(launcher.Started.Last().Detached);
        Assert.Empty(Runs(tab));

        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, "clean"));
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => Runs(tab) is [{ Succeeded: true }], "the clean");

        var clean = Runs(tab).Single();
        Assert.Equal("Clean intermediates", clean.Name);
        Assert.Contains("Deleted 1 folder.", clean.Output);
        Assert.False(Directory.Exists(Path.Combine(h.WorkFolder, "Intermediate")));
    }

    [Fact]
    public async Task Clicking_an_entry_selects_its_tab_and_opens_the_Project_page_on_its_log()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        var build = await RunToEndAsync(tab, launcher, "build-editor", 0);
        var generate = await RunToEndAsync(tab, launcher, "generate-project-files", 0);
        // Another tab is showing. (Its folder is gone, so it starts no session.)
        var other = new TabViewModel(h.Services, h.Shell, new TabState { Folder = Path.Combine(h.Root, "elsewhere") }, isRestored: false);
        var group = new TabGroupViewModel(other.Folder, TabGroupViewModel.Palette[1].Color, isCollapsed: false);
        group.Tabs.Add(other);
        h.Shell.Groups.Add(group);
        h.Shell.SelectedTab = other;
        Assert.False(tab.IsSidePanelOpen);
        Assert.False(build.IsShowing);

        build.OpenCommand.Execute(null);

        Assert.Same(tab, h.Shell.SelectedTab);
        Assert.True(tab.IsSidePanelOpen);
        Assert.True(tab.IsProjectPage);
        Assert.Same(build, tab.ProjectTools.Runs.SelectedRun);
        Assert.True(build.IsShowing);
        Assert.False(generate.IsShowing);

        // The highlight follows the page: another page, or another tab, and no entry is showing.
        tab.ShowFilesPageCommand.Execute(null);
        Assert.False(build.IsShowing);
        tab.ShowProjectPageCommand.Execute(null);
        Assert.True(build.IsShowing);
        h.Shell.SelectedTab = other;
        Assert.False(build.IsShowing);

        // Copy copies the log the page shows.
        await tab.ProjectTools.Runs.CopyOutputCommand.ExecuteAsync(null);
        Assert.EndsWith("Build editor succeeded.", h.Platform.Clipboard, StringComparison.Ordinal);

        // A new job shows itself.
        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, "build-editor"));
        Assert.Same(Runs(tab)[^1], tab.ProjectTools.Runs.SelectedRun);
        launcher.Processes.Last().Exit(0);
    }

    [Fact]
    public async Task A_notification_click_opens_the_Project_page_on_the_run_that_finished()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        var failed = await RunToEndAsync(tab, launcher, "build-editor", 6);
        await TabTestHarness.Eventually(() => h.Notifier.Last("ProjectAction:") is not null, "the notification");
        // In front now: the next one doesn't notify, so the notification is still about the first.
        h.Services.Notifications.SetAppActive(true);
        var shown = h.Notifier.Shown.Count;
        await RunToEndAsync(tab, launcher, "generate-project-files", 0);
        Assert.Equal(shown, h.Notifier.Shown.Count);
        Assert.NotSame(failed, tab.ProjectTools.Runs.SelectedRun);

        InlineDispatcher.Read(() =>
        {
            tab.ProjectTools.Runs.OpenNotifiedRun();
            return true;
        });

        Assert.Same(failed, tab.ProjectTools.Runs.SelectedRun);
        Assert.True(tab.IsSidePanelOpen);
        Assert.True(tab.IsProjectPage);

        // Closing that entry takes its notification away too.
        failed.CloseCommand.Execute(null);
        Assert.Contains(h.Notifier.Removed, id => id.StartsWith("ProjectAction:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closing_the_tab_stops_a_running_job_and_drops_every_entry()
    {
        var (h, launcher, tab) = await OpenAsync();
        await using var _h = h;
        await RunToEndAsync(tab, launcher, "build-editor", 0);
        await tab.ProjectTools.RunActionCommand.ExecuteAsync(Action(tab, "generate-project-files"));
        var running = Runs(tab)[^1];
        var process = launcher.Processes.Last();
        Assert.Equal(2, Runs(tab).Count);
        var notified = h.Notifier.Shown.Count;

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        if (h.Shell.Confirmation is { } confirmation)
        {
            // The job's processes are the tab's: stopped with it.
            await confirmation.ConfirmCommand.ExecuteAsync(null);
        }
        await TabTestHarness.Eventually(() => running.IsStopped, "the stop");

        Assert.Empty(Runs(tab));
        Assert.False(tab.ProjectTools.Runs.HasRuns);
        Assert.Null(tab.ProjectTools.Runs.SelectedRun);
        Assert.True(process.Killed);
        Assert.Equal(notified, h.Notifier.Shown.Count);
    }
}
