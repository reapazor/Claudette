using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Diffs;
using Claudette.Core.Files;
using Claudette.Core.Git;
using Claudette.Core.Processes;
using Claudette.Core.ScratchPads;

namespace Claudette.App.Tests;

/// <summary>
/// The scratch pad (DESIGN.md §18, "Scratch pad"): one per project, shared by its tabs, saved as it's typed in, filled
/// from the conversation, and synced through the session library with another machine's changes.
/// </summary>
public sealed class ScratchPadTests
{
    private const string Remote = "git@github.com:owner/api.git";

    /// <summary>Makes <paramref name="folder"/> a git repository with <paramref name="remote"/>, from files only.</summary>
    private static string MakeRepository(string folder, string? remote = Remote)
    {
        Directory.CreateDirectory(Path.Combine(folder, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(folder, ".git", "config"), remote is null ? "[core]\n" : $"[core]\n[remote \"origin\"]\n\turl = {remote}\n");
        File.WriteAllText(Path.Combine(folder, ".git", "refs", "heads", "main"), "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678\n");
        return folder;
    }

    private static TabTestHarness Harness() => new(settings => settings.Sessions.MachineName = "LAPTOP");

    /// <summary>The pad's file on this machine, once written; null before.</summary>
    private static ScratchPadFile? Saved(TabTestHarness h, ScratchPadViewModel pad) =>
        h.Services.ScratchPads.Store.ReadLocal(pad.Project.Id, h.Time.GetUtcNow());

    /// <summary>
    /// The library's copy of the pad; null when it has none. Read as the app reads it, trying again while a new copy is
    /// being renamed over it, which Windows doesn't allow a plain read during.
    /// </summary>
    private static ScratchPadFile? InLibrary(TabTestHarness h, ScratchPadViewModel pad) =>
        File.Exists(h.Services.ScratchPads.Store.LibraryPath(pad.Project.Id))
            ? ScratchPadFile.Parse(AtomicFile.ReadAllText(h.Services.ScratchPads.Store.LibraryPath(pad.Project.Id)))
            : null;

    /// <summary>Another machine using the same library: types <paramref name="text"/> into its copy of the pad and syncs.</summary>
    private static ScratchPadSyncResult OtherMachineTypes(TabTestHarness h, ScratchPadViewModel pad, string text)
    {
        var other = new ScratchPadStore(Path.Combine(h.Root, "other-machine"), () => h.Services.Library.LibraryFolder);
        var local = other.ReadLocal(pad.Project.Id, h.Time.GetUtcNow()) ?? new ScratchPadFile();
        if (local.Revision.Length == 0)
        {
            local = other.Sync(pad.Project.Id, local).Local;
        }
        local = local with { Text = text, Revision = ScratchPadSync.NewRevision(), Machine = "DESKTOP-01", ChangedAt = h.Time.GetUtcNow() };
        var result = other.Sync(pad.Project.Id, local);
        other.WriteLocal(pad.Project.Id, result.Local);
        return result;
    }

    private static void Type(TabTestHarness h, ScratchPadViewModel pad, string text) => h.Services.Dispatcher.Post(() => pad.Text = text);

    [Fact]
    public async Task Tabs_in_a_project_share_its_pad_and_typing_saves_once_it_pauses()
    {
        await using var h = Harness();
        h.Factory.ProcessPerSession = true;
        MakeRepository(h.WorkFolder);
        var first = await h.OpenTabAsync();
        await h.Shell.OpenFolderAsync(h.WorkFolder);
        var second = h.Shell.SelectedTab!;
        Assert.NotSame(first, second);
        var pad = first.ScratchPad;

        Assert.Same(pad, second.ScratchPad);
        Assert.Equal("Shared by the tabs in work", pad.Summary);
        Type(h, pad, "Check the retry limit");
        Assert.Equal("Check the retry limit", second.ScratchPad.Text);

        // Not while typing goes on.
        h.Time.Advance(ScratchPadViewModel.SaveDelay - TimeSpan.FromMilliseconds(100));
        Type(h, pad, "Check the retry limit and the timeout");
        h.Time.Advance(ScratchPadViewModel.SaveDelay - TimeSpan.FromMilliseconds(100));
        Assert.Null(Saved(h, pad));

        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await TabTestHarness.Eventually(() => Saved(h, pad)?.Text == "Check the retry limit and the timeout", "the pad saved here");
        await TabTestHarness.Eventually(() => InLibrary(h, pad)?.Text == "Check the retry limit and the timeout", "the pad in the library");
        // Then this machine's copy says it matches the library's.
        await TabTestHarness.Eventually(() => Saved(h, pad) is { HasUnsyncedChanges: false }, "the pad saved here as synced");
        var saved = Saved(h, pad)!;
        Assert.Equal(("LAPTOP", "github.com/owner/api"), (saved.Machine, saved.Remote));
    }

    [Fact]
    public async Task Adding_text_opens_the_page_on_it_after_a_blank_line_and_saves_at_once()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();
        var added = new List<(int, int)>();
        tab.ScratchPad.Added += (start, length) => added.Add((start, length));

        tab.AddToScratchPad("First note");
        tab.AddToScratchPad("\n\nSecond note  \n");
        tab.AddToScratchPad("   ");

        Assert.Equal("First note\n\nSecond note\n", tab.ScratchPad.Text);
        Assert.Equal([(0, 10), (12, 11)], added);
        Assert.True(tab.IsSidePanelOpen);
        Assert.True(tab.IsScratchPadPage);
        Assert.Same(tab.ScratchPad, tab.ShownScratchPad);
        Assert.StartsWith("Added to the scratch pad.", h.Shell.Announcement, StringComparison.Ordinal);
        await TabTestHarness.Eventually(() => Saved(h, tab.ScratchPad)?.Text == "First note\n\nSecond note\n", "the pad saved without waiting");
    }

    [Fact]
    public async Task A_message_is_added_as_it_copies_and_a_code_block_in_its_language()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();
        h.Transport.EmitTurn("Use **exponential** backoff.");
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Any(), "the reply");
        var reply = tab.Items.OfType<AssistantTextItem>().Single();

        tab.AddMessageToScratchPadCommand.Execute(reply);
        tab.AddCodeToScratchPad("retry(3);\n", "csharp");

        Assert.Equal("Use **exponential** backoff.\n\n```csharp\nretry(3);\n```\n", tab.ScratchPad.Text);
    }

    [Fact]
    public async Task The_page_is_in_the_palette_and_a_tab_that_never_shows_it_doesnt_read_it()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();

        Assert.Null(tab.ShownScratchPad);
        Assert.Empty(h.Services.ScratchPads.Open());

        await h.Shell.PaletteEntries().Single(e => e.Label == "Show scratch pad").Run();

        Assert.True(tab.IsSidePanelOpen && tab.IsScratchPadPage);
        Assert.NotNull(tab.ShownScratchPad);
    }

    [Fact]
    public async Task Another_machines_change_shows_up_within_a_minute()
    {
        await using var h = Harness();
        var pad = h.Services.ScratchPads.Acquire(MakeRepository(Path.Combine(h.Root, "api")), "api");
        Type(h, pad, "mine");
        h.Time.Advance(ScratchPadViewModel.SaveDelay);
        await TabTestHarness.Eventually(() => InLibrary(h, pad)?.Text == "mine", "the pad in the library");

        Assert.Equal(ScratchPadSyncKind.Pushed, OtherMachineTypes(h, pad, "mine, then theirs").Kind);
        h.Time.Advance(TimeSpan.FromMinutes(1));

        await TabTestHarness.Eventually(() => pad.Text == "mine, then theirs", "the other machine's change");
        Assert.False(pad.HasConflict);
        await TabTestHarness.Eventually(() => Saved(h, pad)?.Text == "mine, then theirs", "it saved here");
    }

    [Theory]
    [InlineData("KeepBoth", "shared\nmine\ntheirs\n")]
    [InlineData("UseTheirs", "shared\ntheirs\n")]
    [InlineData("KeepMine", "shared\nmine\n")]
    public async Task Both_machines_changing_it_asks_which_to_keep(string choice, string expected)
    {
        await using var h = Harness();
        var pad = h.Services.ScratchPads.Acquire(MakeRepository(Path.Combine(h.Root, "api")), "api");
        Type(h, pad, "shared\n");
        h.Time.Advance(ScratchPadViewModel.SaveDelay);
        await TabTestHarness.Eventually(() => InLibrary(h, pad)?.Text == "shared\n", "the pad in the library");
        OtherMachineTypes(h, pad, "shared\ntheirs\n");

        Type(h, pad, "shared\nmine\n");
        h.Time.Advance(ScratchPadViewModel.SaveDelay);

        await TabTestHarness.Eventually(() => pad.HasConflict, "the question");
        Assert.StartsWith("DESKTOP-01 also changed this scratch pad (", pad.ConflictText, StringComparison.Ordinal);
        Assert.Equal("shared\ntheirs\n", pad.TheirText);
        Assert.Equal("shared\nmine\n", pad.Text);
        var command = choice switch
        {
            "KeepBoth" => pad.KeepBothCommand,
            "UseTheirs" => pad.UseTheirsCommand,
            _ => pad.KeepMineCommand,
        };
        h.Services.Dispatcher.Post(() => command.Execute(null));

        await TabTestHarness.Eventually(() => !pad.HasConflict && !pad.IsResolving, "the choice");
        Assert.Equal(expected, pad.Text);
        await TabTestHarness.Eventually(() => InLibrary(h, pad)?.Text == expected, "the choice in the library");
        // The other machine takes it without being asked, unless it's what it had.
        var other = new ScratchPadStore(Path.Combine(h.Root, "other-machine"), () => h.Services.Library.LibraryFolder);
        var theirs = other.Sync(pad.Project.Id, other.ReadLocal(pad.Project.Id, h.Time.GetUtcNow())!);
        Assert.Equal(choice == "UseTheirs" ? ScratchPadSyncKind.InSync : ScratchPadSyncKind.Pulled, theirs.Kind);
        Assert.Equal(expected, theirs.Local.Text);
    }

    [Fact]
    public async Task A_folder_outside_git_keeps_its_pad_on_this_machine()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();
        var pad = tab.ScratchPad;

        Type(h, pad, "local notes");
        h.Time.Advance(ScratchPadViewModel.SaveDelay);

        await TabTestHarness.Eventually(() => Saved(h, pad)?.Text == "local notes", "the pad saved here");
        Assert.False(pad.Project.IsShared);
        Assert.Equal("On this machine only: the folder isn't in a git repository.", pad.SyncText);
        Assert.False(Directory.Exists(Path.Combine(h.Services.Library.LibraryFolder, ScratchPadStore.FolderName)));
    }

    [Fact]
    public async Task Where_a_shared_pad_is_kept_follows_the_library_setting()
    {
        await using var h = Harness();
        var pad = h.Services.ScratchPads.Acquire(MakeRepository(Path.Combine(h.Root, "api")), "api");

        Assert.StartsWith("On this machine. To see it on your other machines too", pad.SyncText, StringComparison.Ordinal);

        h.Services.Settings.Sessions.LibraryFolder = Path.Combine(h.Root, "Google Drive", "Claudette");
        h.Services.SaveSettings();

        Assert.Equal("Synced to your other machines through the session library", pad.SyncText);
    }

    [Fact]
    public async Task Clear_asks_first_then_empties_the_pad()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();
        tab.AddToScratchPad("notes");
        var pad = tab.ScratchPad;

        pad.ClearConfirmation.AskCommand.Execute(null);
        Assert.Equal("notes\n", pad.Text);
        await pad.ClearConfirmation.ConfirmCommand.ExecuteAsync(null);

        Assert.True(pad.IsEmpty);
        await TabTestHarness.Eventually(() => Saved(h, pad)?.Text == "", "the empty pad saved");
    }

    [Fact]
    public async Task Copy_all_puts_the_pad_on_the_clipboard()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();
        var pad = tab.ScratchPad;
        Assert.False(pad.CopyAllCommand.CanExecute(null));
        tab.AddToScratchPad("notes");

        await pad.CopyAllCommand.ExecuteAsync(null);

        Assert.Equal("notes\n", h.Platform.Clipboard);
        Assert.True(pad.IsCopied);
        h.Time.Advance(TabViewModel.CopiedFor);
        Assert.False(pad.IsCopied);
    }

    [Fact]
    public async Task Closing_the_last_tab_in_the_project_saves_what_was_typed()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();
        var pad = tab.ScratchPad;
        Type(h, pad, "typed just before closing");

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);

        await TabTestHarness.Eventually(() => Saved(h, pad)?.Text == "typed just before closing", "the pad saved");
        await TabTestHarness.Eventually(() => !h.Services.ScratchPads.Open().Any(), "the pad let go");
        // Opened again, it's read back.
        var again = await h.OpenTabAsync();
        Assert.Equal("typed just before closing", again.ScratchPad.Text);
    }

    [Fact]
    public async Task Quitting_saves_what_was_typed()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();
        Type(h, tab.ScratchPad, "typed just before quitting");

        await h.Services.FlushAsync();

        Assert.Equal("typed just before quitting", Saved(h, tab.ScratchPad)?.Text);
    }

    [Fact]
    public async Task A_message_with_images_only_adds_nothing()
    {
        await using var h = Harness();
        var tab = await h.OpenTabAsync();

        tab.AddMessageToScratchPadCommand.Execute(new UserMessageItem(""));

        Assert.False(tab.IsSidePanelOpen);
        Assert.Null(tab.ShownScratchPad);
    }

    [Fact]
    public async Task A_tab_that_moves_into_its_worktree_keeps_the_projects_pad()
    {
        Assert.SkipWhen(FileProbe.Instance.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is null, "git isn't on PATH.");
        await using var h = Harness();
        // A new worktree tab asks git where the repository is.
        await GitAsync(h.WorkFolder, "init", "-q");
        await GitAsync(h.WorkFolder, "remote", "add", "origin", Remote);
        await GitAsync(h.WorkFolder, "commit", "-q", "--allow-empty", "-m", "init");
        await h.Shell.OpenWorktreeTabAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the tab to start");
        var pad = tab.ScratchPad;
        // Claude Code makes the worktree, as git lays one out.
        var name = tab.State.NewWorktree!;
        var worktree = GitWorktrees.PathFor(h.WorkFolder, name);
        var gitDir = Path.Combine(h.WorkFolder, ".git", "worktrees", name);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), $"ref: refs/heads/{GitWorktrees.BranchPrefix}{name}\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");

        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5", ["cwd"] = worktree });
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""");

        await TabTestHarness.Eventually(() => tab.Folder == worktree, "the turn in the worktree");
        Assert.Same(pad, tab.ScratchPad);
        Assert.Single(h.Services.ScratchPads.Open());
    }

    private static async Task GitAsync(string folder, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(new ProcessLauncher(),
            new ProcessStartSpec("git", ["-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "commit.gpgsign=false", .. arguments]) { WorkingDirectory = folder },
            TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
