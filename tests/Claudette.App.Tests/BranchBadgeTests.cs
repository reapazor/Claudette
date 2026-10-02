using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Diffs;
using Claudette.Core.Git;
using Claudette.Core.Processes;

namespace Claudette.App.Tests;

/// <summary>
/// The git branch on a tab's row (DESIGN.md §4, "Sidebar"): what the tab's folder has checked out, the worktree's name
/// in a worktree, a short commit when detached, and following a switch.
/// </summary>
public sealed class BranchBadgeTests
{
    private const string Commit = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    /// <summary>Makes <paramref name="folder"/> a git repository on <paramref name="head"/>, from files only.</summary>
    private static void MakeRepository(string folder, string head = "ref: refs/heads/main")
    {
        Directory.CreateDirectory(Path.Combine(folder, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(folder, ".git", "config"), "[core]\n");
        File.WriteAllText(Path.Combine(folder, ".git", "refs", "heads", "main"), Commit + "\n");
        SetHead(folder, head);
    }

    private static void SetHead(string folder, string head) => File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), head + "\n");

    [Fact]
    public async Task A_tab_in_a_repository_shows_its_branch_and_copies_it()
    {
        await using var h = new TabTestHarness();
        MakeRepository(h.WorkFolder);
        var tab = await h.OpenTabAsync();

        Assert.Equal("main", tab.BranchBadge);
        Assert.True(tab.ShowBranchBadge);
        Assert.False(tab.ShowWorktreeIcon);
        Assert.Equal("On branch main. Click to copy the branch's name.", tab.BranchBadgeTip);

        await tab.CopyBranchCommand.ExecuteAsync(null);
        Assert.Equal("main", h.Platform.Clipboard);
    }

    [Fact]
    public async Task A_branch_switched_during_a_turn_shows_once_it_ends()
    {
        await using var h = new TabTestHarness();
        MakeRepository(h.WorkFolder);
        var tab = await h.OpenTabAsync();
        var changes = new List<string?>();
        tab.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        tab.ComposerText = "switch to the feature branch";
        await tab.SendCommand.ExecuteAsync(null);
        SetHead(h.WorkFolder, "ref: refs/heads/feature/auth");
        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() => tab.BranchBadge == "feature/auth", "the new branch");
        Assert.Contains(nameof(TabViewModel.BranchBadge), changes);
    }

    [Fact]
    public async Task A_detached_head_shows_its_short_commit()
    {
        await using var h = new TabTestHarness();
        MakeRepository(h.WorkFolder, head: Commit);
        var tab = await h.OpenTabAsync();

        Assert.Equal("a1b2c3d", tab.BranchBadge);
        Assert.Equal("At a1b2c3d, not on a branch. Click to copy the branch's name.", tab.BranchBadgeTip);
    }

    [Fact]
    public async Task Outside_a_repository_there_is_no_badge()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        Assert.Null(tab.BranchBadge);
        Assert.False(tab.ShowBranchBadge);
    }

    [Fact]
    public async Task The_setting_hides_it()
    {
        await using var h = new TabTestHarness();
        MakeRepository(h.WorkFolder);
        var tab = await h.OpenTabAsync();

        h.Services.Settings.Appearance.ShowBranchOnTabs = false;
        h.Services.SaveSettings();

        Assert.False(tab.ShowBranchBadge);
        Assert.Equal("main", tab.BranchBadge);
    }

    [Fact]
    public async Task A_worktree_tab_shows_its_worktrees_name_with_the_branch_icon_in_the_badge()
    {
        Assert.SkipWhen(FileProbe.Instance.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is null, "git isn't on PATH.");
        await using var h = new TabTestHarness();
        await GitAsync(h.WorkFolder, "init", "-q", "-b", "main");
        await GitAsync(h.WorkFolder, "commit", "-q", "--allow-empty", "-m", "init");
        await h.Shell.OpenWorktreeTabAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the tab to start");
        var name = tab.State.NewWorktree!;

        // Before Claude Code has made it: its name, and the branch it'll be on to copy.
        Assert.Equal(name, tab.BranchBadge);
        Assert.False(tab.ShowWorktreeIcon);
        await tab.CopyBranchCommand.ExecuteAsync(null);
        Assert.Equal(GitWorktrees.BranchPrefix + name, h.Platform.Clipboard);

        // Made, as git lays one out, and reported by the first turn.
        var worktree = GitWorktrees.PathFor(h.WorkFolder, name);
        await GitAsync(h.WorkFolder, "worktree", "add", "-q", "-b", GitWorktrees.BranchPrefix + name, worktree);
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5", ["cwd"] = worktree });
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => tab.Folder == worktree, "the turn in the worktree");

        Assert.Equal(name, tab.BranchBadge);
        Assert.Equal($"Works in the worktree {name}, on branch {GitWorktrees.BranchPrefix}{name}. Click to copy the branch's name.", tab.BranchBadgeTip);
        // With the badge off, the worktree's icon is back on the name line.
        h.Services.Settings.Appearance.ShowBranchOnTabs = false;
        h.Services.SaveSettings();
        Assert.True(tab.ShowWorktreeIcon);
    }

    private static async Task GitAsync(string folder, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(new ProcessLauncher(),
            new ProcessStartSpec("git", ["-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "commit.gpgsign=false", .. arguments]) { WorkingDirectory = folder },
            TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
