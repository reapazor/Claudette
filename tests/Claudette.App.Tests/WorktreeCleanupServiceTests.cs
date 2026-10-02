using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Files;
using Claudette.Core.Git;
using Claudette.Core.Processes;
using Claudette.Core.ProjectTools;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// Setting up a new worktree with the folder's actions (DESIGN.md §18, "Setting up a new worktree"), and removing
/// worktrees no tab needs (DESIGN.md §4, "Cleaning up worktrees"), against real git repositories in temporary folders.
/// </summary>
public class WorktreeCleanupServiceTests
{
    private static readonly bool GitInstalled = WorktreeTabTests.GitInstalled;

    /// <summary>Git runs for real; everything else (the actions) is a fake process the test controls.</summary>
    private sealed class GitAndFakeLauncher : IProcessLauncher
    {
        private readonly ProcessLauncher _real = new();

        public FakeLauncher Fake { get; } = new();

        public IRunningProcess Start(ProcessStartSpec spec) =>
            Path.GetFileNameWithoutExtension(spec.FileName).Equals("git", StringComparison.OrdinalIgnoreCase) ? _real.Start(spec) : Fake.Start(spec);
    }

    private static string Local(TabTestHarness h) => Path.Combine(h.WorkFolder, ProjectFile.LocalName);

    private static string Shared(TabTestHarness h) => Path.Combine(h.WorkFolder, ProjectFile.SharedName);

    // ---- Setting up a new worktree ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_worktree_runs_the_actions_for_new_worktrees_in_it_one_after_another()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        var launcher = new GitAndFakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        await WorktreeTabTests.InitRepositoryAsync(h);
        await File.WriteAllTextAsync(Local(h), """
            { "actions": [
                { "name": "Install", "command": "npm ci", "runOnNewWorktree": true },
                { "name": "Test", "command": "npm test" },
                { "name": "Copy env", "command": "copy env", "runOnNewWorktree": true }
            ] }
            """, TestContext.Current.CancellationToken);

        var (tab, path) = await WorktreeTabTests.WorktreeTabAsync(h, realWorktree: false);

        await TabTestHarness.Eventually(() => launcher.Fake.Started.Count == 1, "the first setup action");
        var install = launcher.Fake.Started[0];
        Assert.True(FolderHistory.SamePath(path, install.WorkingDirectory!));
        Assert.True(FolderHistory.SamePath(path, install.Environment![CustomProjectAction.WorktreeVariable]));
        Assert.True(FolderHistory.SamePath(h.WorkFolder, install.Environment[CustomProjectAction.MainCheckoutVariable]));
        Assert.Contains(tab.Items.OfType<NoteItem>(), n => n.Text == "Setting up the new worktree: Install, Copy env.");
        Assert.Equal("Install", tab.ProjectTools.Runs.RunningRun?.Name);

        // The next waits for it to work.
        await Waiting.NeverAsync(() => launcher.Fake.Started.Count > 1, "the next action before the first ends", TimeSpan.FromMilliseconds(200));
        launcher.Fake.Processes[0].Exit(0);
        await TabTestHarness.Eventually(() => launcher.Fake.Started.Count == 2, "the second setup action");
        Assert.True(FolderHistory.SamePath(path, launcher.Fake.Started[1].Environment![CustomProjectAction.WorktreeVariable]));
    }

    [Fact]
    public async Task A_shared_files_actions_run_only_in_a_trusted_folder()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        var launcher = new GitAndFakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        h.Factory.ProcessPerSession = true;
        await WorktreeTabTests.InitRepositoryAsync(h);
        await File.WriteAllTextAsync(Shared(h), """{ "actions": [ { "name": "Install", "command": "npm ci", "runOnNewWorktree": true } ] }""", TestContext.Current.CancellationToken);

        var (tab, _) = await WorktreeTabTests.WorktreeTabAsync(h, realWorktree: false);

        await TabTestHarness.Eventually(() => tab.Items.OfType<NoteItem>().Any(n => n.Text.StartsWith("claudette.json's actions for a new worktree didn't run", StringComparison.Ordinal)), "the note");
        Assert.Empty(launcher.Fake.Started);

        // Trusted, the next worktree's run.
        h.Services.TrustFolder(h.WorkFolder);
        await WorktreeTabTests.WorktreeTabAsync(h, realWorktree: false);
        await TabTestHarness.Eventually(() => launcher.Fake.Started.Count == 1, "the trusted setup action");
    }

    [Fact]
    public async Task A_worktree_opened_again_after_a_restart_isnt_set_up_twice()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        var launcher = new GitAndFakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        await WorktreeTabTests.InitRepositoryAsync(h);
        await File.WriteAllTextAsync(Local(h), """{ "actions": [ { "name": "Install", "command": "npm ci", "runOnNewWorktree": true } ] }""", TestContext.Current.CancellationToken);
        await h.Shell.OpenWorktreeTabAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the tab to start");
        var name = tab.State.NewWorktree!;
        var path = GitWorktrees.PathFor(h.WorkFolder, name);

        // Made by the first start, which then stopped before its first turn: the next start opens it again.
        Directory.CreateDirectory(path);
        h.Transport.Exit(0);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Exited, "the stop");
        h.Transport.RestartIfExited();
        await tab.RestartCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the start again");
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(new System.Text.Json.Nodes.JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5", ["cwd"] = path });
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => tab.Folder == path && tab.Status != TabStatus.Working, "the turn in the worktree");

        await Waiting.NeverAsync(() => launcher.Fake.Started.Count > 0, "a setup action", TimeSpan.FromMilliseconds(200));
    }

    // ---- Cleaning up worktrees -------------------------------------------------------------------------------------

    /// <summary>A worktree as Claude Code makes one for a worktree tab: on its own branch, locked.</summary>
    private static async Task<string> KeptWorktreeAsync(TabTestHarness h, string name)
    {
        var path = GitWorktrees.PathFor(h.WorkFolder, name);
        await WorktreeTabTests.GitAsync(h.WorkFolder, "worktree", "add", "-q", "-b", GitWorktrees.BranchPrefix + name, path);
        await WorktreeTabTests.GitAsync(h.WorkFolder, "worktree", "lock", "--reason", $"claude session {name} (pid 1)", path);
        return path;
    }

    private static async Task<TabTestHarness> RepositoryAsync(Action<AppSettings> rules)
    {
        var h = new TabTestHarness(rules);
        await WorktreeTabTests.InitRepositoryAsync(h);
        // It's a recent folder, so its repository is looked in.
        FolderHistory.Touch(h.Services.State, h.WorkFolder, h.Time.GetUtcNow(), 20);
        return h;
    }

    /// <summary>Replaces the time kept for the worktree, which a pass may have written under git's spelling of its path.</summary>
    private static void LastUsed(TabTestHarness h, string path, TimeSpan ago)
    {
        var times = h.Services.State.WorktreesLastUsed;
        foreach (var key in times.Keys.Where(k => RealPath.Same(k, path)).ToList())
        {
            times.Remove(key);
        }
        times[FolderHistory.Normalize(path)] = h.Time.GetUtcNow() - ago;
    }

    private static async Task<bool> BranchExistsAsync(TabTestHarness h, string branch) =>
        (await new GitWorktrees(h.Services.Git).BranchesAsync(h.WorkFolder)).Contains(branch);

    [Fact]
    public async Task A_merged_worktree_goes_with_its_branch_a_day_after_it_was_last_used()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = await RepositoryAsync(s => s.General.RemoveMergedWorktrees = true);
        var path = await KeptWorktreeAsync(h, "brisk-otter");

        // First seen now: it waits a day. Git names the worktree by its real path (on macOS, the temporary folder's
        // /var is /private/var), so the paths are matched through their links.
        Assert.Empty(await h.Services.Worktrees.RunAsync());
        Assert.True(Directory.Exists(path));
        Assert.Contains(h.Services.State.WorktreesLastUsed.Keys, k => RealPath.Same(k, path));

        LastUsed(h, path, TimeSpan.FromDays(1));
        var removed = await h.Services.Worktrees.RunAsync();

        Assert.True(RealPath.Same(path, Assert.Single(removed)));
        Assert.False(Directory.Exists(path));
        Assert.False(await BranchExistsAsync(h, "worktree-brisk-otter"));
        Assert.Single(h.Services.State.LastWorktreeCleanup!.Removed);
        Assert.DoesNotContain(h.Services.State.WorktreesLastUsed.Keys, k => RealPath.Same(k, path));
    }

    [Fact]
    public async Task An_unused_worktree_with_commits_of_its_own_goes_and_its_branch_stays()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = await RepositoryAsync(s => (s.General.RemoveMergedWorktrees, s.General.RemoveInactiveWorktreesAfterDays) = (true, 7));
        var path = await KeptWorktreeAsync(h, "calm-heron");
        await WorktreeTabTests.GitAsync(path, "commit", "-q", "--allow-empty", "-m", "work of its own");

        LastUsed(h, path, TimeSpan.FromDays(6));
        Assert.Empty(await h.Services.Worktrees.RunAsync());

        LastUsed(h, path, TimeSpan.FromDays(7));
        Assert.Single(await h.Services.Worktrees.RunAsync());
        Assert.False(Directory.Exists(path));
        Assert.True(await BranchExistsAsync(h, "worktree-calm-heron"));
    }

    [Fact]
    public async Task Uncommitted_work_and_a_worktree_a_tab_works_in_are_kept()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = await RepositoryAsync(s => (s.General.RemoveMergedWorktrees, s.General.RemoveInactiveWorktreesAfterDays) = (true, 7));
        var dirty = await KeptWorktreeAsync(h, "amber-fern");
        await File.WriteAllTextAsync(Path.Combine(dirty, "notes.txt"), "not committed", TestContext.Current.CancellationToken);
        var open = await KeptWorktreeAsync(h, "jolly-wren");
        await h.Shell.OpenFolderAsync(open);
        LastUsed(h, dirty, TimeSpan.FromDays(100));
        LastUsed(h, open, TimeSpan.FromDays(100));

        Assert.Empty(await h.Services.Worktrees.RunAsync());

        Assert.True(Directory.Exists(dirty));
        Assert.True(Directory.Exists(open));
        // The open tab's worktree was used just now.
        Assert.Equal(h.Time.GetUtcNow(), h.Services.State.WorktreesLastUsed.Single(e => FolderHistory.SamePath(e.Key, open)).Value);
    }

    [Fact]
    public async Task Nothing_is_looked_at_while_both_rules_are_off()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = await RepositoryAsync(_ => { });
        var path = await KeptWorktreeAsync(h, "quiet-pine");
        LastUsed(h, path, TimeSpan.FromDays(365));

        Assert.Empty(await h.Services.Worktrees.RunAsync());
        Assert.True(Directory.Exists(path));
        Assert.Null(h.Services.State.LastWorktreeCleanup);
    }

    [Fact]
    public async Task The_first_pass_runs_a_minute_after_launch()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = await RepositoryAsync(s => s.General.RemoveMergedWorktrees = true);
        var path = await KeptWorktreeAsync(h, "swift-raven");
        LastUsed(h, path, TimeSpan.FromDays(2));
        h.Shell.Restore(null);

        h.Time.Advance(WorktreeCleanupService.FirstRunDelay);

        await TabTestHarness.Eventually(() => !Directory.Exists(path), "the first pass");
    }

    [Fact]
    public async Task Closing_a_worktree_tab_marks_its_worktree_used_then()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = await RepositoryAsync(s => s.General.RemoveMergedWorktrees = true);
        var (tab, path) = await WorktreeTabTests.WorktreeTabAsync(h, realWorktree: true);
        h.Time.Advance(TimeSpan.FromHours(3));

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        h.Shell.Confirmation?.CancelCommand.Execute(null);

        Assert.Equal(h.Time.GetUtcNow(), h.Services.State.WorktreesLastUsed.Single(e => FolderHistory.SamePath(e.Key, path)).Value);
    }
}
