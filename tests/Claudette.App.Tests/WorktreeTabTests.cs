using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Diffs;
using Claudette.Core.Git;
using Claudette.Core.Processes;

namespace Claudette.App.Tests;

/// <summary>
/// Worktree tabs and extra folders (DESIGN.md §4): a tab whose Claude Code works in a git worktree of its own, removing
/// that worktree when its last tab closes, and the folders Claude may also use.
/// </summary>
public class WorktreeTabTests
{
    private static readonly bool GitInstalled = FileProbe.Instance.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is not null;

    private static async Task GitAsync(string folder, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(new ProcessLauncher(),
            new ProcessStartSpec("git", ["-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "commit.gpgsign=false", .. arguments]) { WorkingDirectory = folder },
            TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    private static async Task InitRepositoryAsync(TabTestHarness h)
    {
        await GitAsync(h.WorkFolder, "init", "-q");
        await GitAsync(h.WorkFolder, "commit", "-q", "--allow-empty", "-m", "init");
    }

    /// <summary>
    /// Opens a worktree tab and plays Claude Code's part: <c>--worktree</c> makes the worktree (with git, locked, as in
    /// <c>-p</c> mode, when <paramref name="realWorktree"/>), and the first turn's <c>system/init</c> reports it.
    /// </summary>
    private static async Task<(TabViewModel Tab, string Path)> WorktreeTabAsync(TabTestHarness h, bool realWorktree)
    {
        await h.Shell.OpenWorktreeTabAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the tab to start");
        var name = tab.State.NewWorktree!;
        var path = GitWorktrees.PathFor(h.WorkFolder, name);
        if (realWorktree)
        {
            await GitAsync(h.WorkFolder, "worktree", "add", "-q", "-b", GitWorktrees.BranchPrefix + name, path);
            await GitAsync(h.WorkFolder, "worktree", "lock", "--reason", $"claude session {name} (pid 1)", path);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5", ["cwd"] = path });
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.Working && tab.Folder == path, "the turn in the worktree");
        return (tab, path);
    }

    [Fact]
    public async Task A_worktree_tab_starts_in_a_named_worktree_and_carries_on_there_in_its_folders_group()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = new TabTestHarness();
        await InitRepositoryAsync(h);

        var (tab, path) = await WorktreeTabAsync(h, realWorktree: false);

        var first = h.Factory.Launches[0];
        Assert.Equal(h.WorkFolder, first.WorkingDirectory);
        Assert.Equal(Path.GetFileName(path), first.Worktree);
        Assert.Matches("^[a-z]+-[a-z]+$", first.Worktree);
        Assert.Null(tab.State.NewWorktree);
        Assert.Equal(h.WorkFolder, tab.State.WorktreeOf);
        Assert.True(tab.IsInWorktree);
        Assert.Equal(Path.GetFileName(path), tab.WorktreeName);
        Assert.Equal(h.WorkFolder, Assert.Single(h.Shell.Groups).Folder);
        Assert.Contains(tab.InfoRows, r => r.Label == "Worktree" && r.Value.StartsWith(Path.GetFileName(path), StringComparison.Ordinal));
        Assert.Contains(tab.Items.OfType<Conversation.NoteItem>(), n => n.Text.StartsWith("Working in the worktree", StringComparison.Ordinal));

        // Started again, it resumes in the worktree rather than making another.
        await ((IRemoteControlHost)tab).RestartSessionAsync();
        var again = h.Factory.Launches[^1];
        Assert.Null(again.Worktree);
        Assert.Equal(path, again.WorkingDirectory);
        Assert.Equal("s1", again.Resume);
    }

    [Fact]
    public async Task Only_a_folder_in_a_git_repository_can_have_a_worktree_tab()
    {
        await using var h = new TabTestHarness();

        await h.Shell.OpenWorktreeTabAsync(h.WorkFolder);

        Assert.Empty(h.Shell.AllTabs);
        Assert.Empty(h.Factory.Launches);
        Assert.StartsWith("work isn't in a git repository", h.Shell.Confirmation?.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_last_tab_in_an_empty_worktree_offers_to_remove_it_and_its_branch()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = new TabTestHarness();
        await InitRepositoryAsync(h);
        var (tab, path) = await WorktreeTabAsync(h, realWorktree: true);

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await TabTestHarness.Eventually(() => h.Shell.Confirmation is not null, "the offer");

        var offer = h.Shell.Confirmation!;
        Assert.Equal($"Remove the worktree {Path.GetFileName(path)}?", offer.Title);
        Assert.Equal("Keep", offer.CancelText);
        await offer.ConfirmCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(path));
        Assert.Null(await new GitWorktrees(h.Services.Git).FindAsync(h.WorkFolder, path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_worktree_holding_work_is_kept_unless_the_user_discards_it()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = new TabTestHarness();
        await InitRepositoryAsync(h);
        var (tab, path) = await WorktreeTabAsync(h, realWorktree: true);
        await File.WriteAllTextAsync(Path.Combine(path, "draft.md"), "work\n", TestContext.Current.CancellationToken);

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await TabTestHarness.Eventually(() => h.Shell.Confirmation is not null, "the offer");

        var offer = h.Shell.Confirmation!;
        Assert.Equal($"Keep the worktree {Path.GetFileName(path)}?", offer.Title);
        Assert.Contains("uncommitted changes", offer.Message, StringComparison.Ordinal);
        Assert.False(offer.HasCancel);
        Assert.Equal("Remove and discard", offer.SecondaryText);
        await offer.SecondaryCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task Another_tab_still_in_the_worktree_keeps_it_without_asking()
    {
        Assert.SkipWhen(!GitInstalled, "git isn't on PATH.");
        await using var h = new TabTestHarness();
        await InitRepositoryAsync(h);
        var (tab, path) = await WorktreeTabAsync(h, realWorktree: true);
        h.Factory.ProcessPerSession = true;
        await h.Shell.DuplicateTabCommand.ExecuteAsync(tab);
        var copy = h.Shell.SelectedTab!;
        Assert.NotSame(tab, copy);
        Assert.Equal(path, copy.Folder);
        Assert.Equal(h.WorkFolder, copy.GroupFolder);

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);

        await Waiting.NeverAsync(() => h.Shell.Confirmation is not null, "an offer");
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Extra_folders_are_passed_to_Claude_Code_which_starts_again_to_take_a_change()
    {
        await using var h = new TabTestHarness();
        var extra = Directory.CreateDirectory(Path.Combine(h.Root, "lib")).FullName;
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.State.SessionId == "s1", "the turn");
        var launches = h.Factory.Launches.Count;

        await tab.SetExtraFoldersAsync([extra]);

        Assert.Equal(launches + 1, h.Factory.Launches.Count);
        Assert.Equal([extra], h.Factory.Launches[^1].AddDirectories);
        Assert.Equal("s1", h.Factory.Launches[^1].Resume);
        Assert.Contains(tab.InfoRows, r => r.Label == "Extra folders" && r.Value == extra);

        // During a turn, it waits for the turn to end.
        tab.ComposerText = "more";
        await tab.SendCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");
        await tab.SetExtraFoldersAsync([]);
        Assert.Equal(launches + 1, h.Factory.Launches.Count);
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == launches + 2, "the restart after the turn");
        Assert.Empty(h.Factory.Launches[^1].AddDirectories);
    }

    [Fact]
    public async Task Tab_settings_add_and_remove_extra_folders()
    {
        await using var h = new TabTestHarness();
        var extra = Directory.CreateDirectory(Path.Combine(h.Root, "docs")).FullName;
        var tab = await h.OpenTabAsync();
        var settings = new TabSettingsViewModel(h.Services, tab, () => { });

        h.Platform.FolderToPick = extra;
        await settings.AddExtraFolderCommand.ExecuteAsync(null);
        await settings.AddExtraFolderCommand.ExecuteAsync(null);
        // The tab's own folder isn't an extra one.
        h.Platform.FolderToPick = h.WorkFolder;
        await settings.AddExtraFolderCommand.ExecuteAsync(null);
        Assert.Equal([extra], settings.ExtraFolders);

        await settings.ApplyCommand.ExecuteAsync(null);
        Assert.Equal([extra], tab.State.ExtraFolders);

        settings = new TabSettingsViewModel(h.Services, tab, () => { });
        settings.RemoveExtraFolderCommand.Execute(extra);
        await settings.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(tab.State.ExtraFolders);
    }
}
