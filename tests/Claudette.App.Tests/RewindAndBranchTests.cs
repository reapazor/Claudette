using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Going back to an earlier message: edit and resend, branch from here, restore files, duplicate tab (DESIGN.md §5, "Rewind and branch").</summary>
public sealed class RewindAndBranchTests
{
    /// <summary>Two turns: "first" (u1) answered by a1, then "second" (u2, after a1's attachment) answered by a2.</summary>
    private static void WriteTwoTurns(TabTestHarness h) => h.WriteTranscript("s1",
        Entry("user", "u1", null, "first"),
        Entry("assistant", "a1", "u1", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "one" })),
        new JsonObject { ["type"] = "attachment", ["uuid"] = "att1", ["parentUuid"] = "a1", ["sessionId"] = "s1" }.ToJsonString(),
        Entry("user", "u2", "att1", "second"),
        Entry("assistant", "a2", "u2", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "two" })));

    private static string Entry(string type, string uuid, string? parent, JsonNode content) => new JsonObject
    {
        ["type"] = type,
        ["uuid"] = uuid,
        ["parentUuid"] = parent,
        ["sessionId"] = "s1",
        ["timestamp"] = "2026-09-28T10:00:00Z",
        ["message"] = new JsonObject { ["role"] = type, ["content"] = content },
    }.ToJsonString();

    private static async Task<TabViewModel> RestoreAsync(TabTestHarness h)
    {
        WriteTwoTurns(h);
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "s1" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the restored tab");
        return tab;
    }

    private static UserMessageItem Prompt(TabViewModel tab, string text) => tab.Items.OfType<UserMessageItem>().Single(m => m.Text == text);

    private static IEnumerable<JsonObject> RewindRequests(TabTestHarness h) =>
        h.Transport.Sent.Where(m => m["request"]?["subtype"]?.GetValue<string>() == "rewind_files").Select(m => m["request"]!.AsObject());

    [Fact]
    public async Task A_restored_prompt_knows_its_id_and_the_point_before_it()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);

        Assert.Equal(("u1", (string?)null), (Prompt(tab, "first").Uuid, Prompt(tab, "first").ResumeAt));
        // The entry before it may be one that shows nothing, such as an attachment.
        Assert.Equal(("u2", "att1"), (Prompt(tab, "second").Uuid, Prompt(tab, "second").ResumeAt));
    }

    [Fact]
    public async Task Edit_and_resend_goes_back_to_before_the_message_as_a_copy_and_puts_it_in_the_composer()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);

        await tab.EditAndResendCommand.ExecuteAsync(Prompt(tab, "second"));
        var confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Equal("Edit and resend this message?", confirmation.Title);
        // Claude Code has no checkpoint to restore: nothing to choose about files.
        Assert.Equal("Go back", confirmation.ConfirmText);
        Assert.False(confirmation.HasSecondary);
        await confirmation.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.Status == TabStatus.Idle && tab.IsSettled, "the restart");

        var launch = h.Factory.Launches[1];
        Assert.Equal("s1", launch.Resume);
        Assert.True(launch.ForkSession);
        Assert.Equal("att1", launch.ResumeSessionAt);
        // The last prompt: Claude Code checks it's the only one left out.
        Assert.Equal("u2", launch.ResumeDropsTurn);
        Assert.Equal("second", tab.ComposerText);
        Assert.Equal(["first"], tab.Items.OfType<UserMessageItem>().Select(m => m.Text));
        Assert.Contains(tab.Items, i => i is AssistantTextItem { Text: "one" });
        Assert.DoesNotContain(tab.Items, i => i is AssistantTextItem { Text: "two" });
        var note = Assert.IsType<NoteItem>(tab.Items[^1]);
        Assert.Equal("Went back to before your message. Edit it and send it again.", note.Text);
        Assert.DoesNotContain(tab.Items, i => i is NoteItem { Text: "Resumed." });
        // The point is used once: the next start is a plain resume of the copy.
        Assert.Null(tab.State.ResumeAt);
        Assert.False(tab.State.ForkOnNextStart);
    }

    [Fact]
    public async Task Edit_and_resend_of_an_earlier_message_leaves_out_the_later_ones_without_the_last_turn_guard()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);
        tab.ComposerText = "third";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn("three");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the turn");

        await tab.EditAndResendCommand.ExecuteAsync(Prompt(tab, "second"));
        Assert.Contains("leaving out the one after it", h.Shell.Confirmation!.Message, StringComparison.Ordinal);
        await h.Shell.Confirmation.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.IsSettled, "the restart");

        Assert.Equal("att1", h.Factory.Launches[1].ResumeSessionAt);
        Assert.Null(h.Factory.Launches[1].ResumeDropsTurn);
    }

    [Fact]
    public async Task Edit_and_resend_of_the_first_message_starts_a_new_session()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);

        await tab.EditAndResendCommand.ExecuteAsync(Prompt(tab, "first"));
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.Status == TabStatus.Idle && tab.IsSettled, "the restart");

        Assert.Null(h.Factory.Launches[1].Resume);
        Assert.Null(tab.State.SessionId);
        Assert.Empty(tab.Items.OfType<UserMessageItem>());
        Assert.Equal("first", tab.ComposerText);
    }

    [Fact]
    public async Task Edit_and_resend_can_put_the_files_back_too()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);
        var file = Path.Combine(h.WorkFolder, "a.cs");
        h.Transport.Answers["rewind_files"] = _ => new JsonObject { ["canRewind"] = true, ["filesChanged"] = new JsonArray(file), ["insertions"] = 3, ["deletions"] = 1 };

        await tab.EditAndResendCommand.ExecuteAsync(Prompt(tab, "second"));
        var confirmation = h.Shell.Confirmation!;
        Assert.Equal(("Go back and restore files", "Go back, keep files"), (confirmation.ConfirmText, confirmation.SecondaryText));
        Assert.Contains("Claude changed a.cs since", confirmation.Message, StringComparison.Ordinal);
        await confirmation.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.IsSettled, "the restart");

        var requests = RewindRequests(h).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.True(requests[0]["dry_run"]!.GetValue<bool>());
        Assert.Equal("u2", requests[1]["user_message_id"]!.GetValue<string>());
        Assert.Null(requests[1]["dry_run"]);
        Assert.Contains(tab.Items, i => i is NoteItem { Text: "Went back to before your message, and put the files back as they were then. Edit it and send it again." });
    }

    [Fact]
    public async Task Go_back_keep_files_leaves_them()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);
        h.Transport.Answers["rewind_files"] = _ => new JsonObject { ["canRewind"] = true, ["filesChanged"] = new JsonArray("a.cs", "b.cs") };

        await tab.EditAndResendCommand.ExecuteAsync(Prompt(tab, "second"));
        Assert.Contains("Claude changed a.cs and b.cs since", h.Shell.Confirmation!.Message, StringComparison.Ordinal);
        await h.Shell.Confirmation.SecondaryCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.IsSettled, "the restart");

        // Only the dry run.
        Assert.Single(RewindRequests(h));
        Assert.Equal("second", tab.ComposerText);
    }

    [Fact]
    public async Task Claude_working_keeps_the_conversation_where_it_is()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);
        tab.Status = TabStatus.Working;

        await tab.EditAndResendCommand.ExecuteAsync(Prompt(tab, "second"));

        Assert.Null(h.Shell.Confirmation);
        Assert.Contains(tab.Items, i => i is NoteItem n && n.Text.StartsWith("Claude is working.", StringComparison.Ordinal));
        Assert.Single(h.Factory.Launches);
    }

    [Fact]
    public async Task Restore_files_lists_them_and_puts_them_back_leaving_the_conversation()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);
        h.Transport.Answers["rewind_files"] = _ => new JsonObject { ["canRewind"] = true, ["filesChanged"] = new JsonArray("src/a.cs"), ["insertions"] = 3, ["deletions"] = 1 };

        await tab.RestoreFilesBeforeCommand.ExecuteAsync(Prompt(tab, "first"));
        var confirmation = h.Shell.Confirmation!;
        Assert.Equal("Restore files to before this message?", confirmation.Title);
        Assert.StartsWith("Claude Code puts a.cs (+3 −1) back as it was before it.", confirmation.Message, StringComparison.Ordinal);
        await confirmation.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("u1", RewindRequests(h).Last()["user_message_id"]!.GetValue<string>());
        Assert.Contains(tab.Items, i => i is NoteItem { Text: "Put a.cs back as before that message." });
        Assert.Equal(2, tab.Items.OfType<UserMessageItem>().Count());
        Assert.Single(h.Factory.Launches);
    }

    [Fact]
    public async Task Restore_files_says_when_there_is_no_checkpoint()
    {
        await using var h = new TabTestHarness();
        var tab = await RestoreAsync(h);
        h.Transport.Answers["rewind_files"] = _ => new JsonObject { ["canRewind"] = false, ["error"] = "No file checkpoint found for this message" };

        await tab.RestoreFilesBeforeCommand.ExecuteAsync(Prompt(tab, "first"));

        Assert.Null(h.Shell.Confirmation);
        Assert.Contains(tab.Items, i => i is NoteItem n && n.Text.StartsWith("Claude Code can't put files back to before that message: No file checkpoint found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Branch_from_here_opens_a_copy_up_to_the_message_with_it_in_the_composer()
    {
        await using var h = new TabTestHarness();
        // The new tab runs alongside the first.
        h.Factory.ProcessPerSession = true;
        var tab = await RestoreAsync(h);
        tab.State.Overrides.Model = "sonnet";
        tab.State.KeptSuffixes.Add("tests");

        await tab.BranchFromHereCommand.ExecuteAsync(Prompt(tab, "second"));
        var branch = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && branch.Status == TabStatus.Idle && branch.IsSettled, "the branch");

        Assert.NotSame(tab, branch);
        Assert.Equal(h.WorkFolder, branch.Folder);
        var launch = h.Factory.Launches[1];
        Assert.Equal(("s1", true, "att1", (string?)null), (launch.Resume, launch.ForkSession, launch.ResumeSessionAt, launch.ResumeDropsTurn));
        Assert.Equal("sonnet", launch.Model);
        Assert.Equal(["tests"], branch.State.KeptSuffixes);
        Assert.Equal("second", branch.ComposerText);
        Assert.Equal(["first"], branch.Items.OfType<UserMessageItem>().Select(m => m.Text));
        Assert.Contains(branch.Items, i => i is NoteItem { Text: "Opened as a copy. The original session is left as it was." });
        // The tab it came from is as it was.
        Assert.Equal(["first", "second"], tab.Items.OfType<UserMessageItem>().Select(m => m.Text));
        Assert.Equal("", tab.ComposerText);
    }

    [Fact]
    public async Task Branch_from_the_first_message_starts_a_new_session()
    {
        await using var h = new TabTestHarness();
        // The new tab runs alongside the first.
        h.Factory.ProcessPerSession = true;
        var tab = await RestoreAsync(h);

        await tab.BranchFromHereCommand.ExecuteAsync(Prompt(tab, "first"));
        var branch = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && branch.IsSettled, "the branch");

        Assert.Null(h.Factory.Launches[1].Resume);
        Assert.Equal("first", branch.ComposerText);
        Assert.Empty(branch.Items.OfType<UserMessageItem>());
    }

    [Fact]
    public async Task Duplicate_tab_carries_the_whole_session_on_as_a_copy()
    {
        await using var h = new TabTestHarness();
        // The new tab runs alongside the first.
        h.Factory.ProcessPerSession = true;
        var tab = await RestoreAsync(h);

        await h.Shell.DuplicateTabCommand.ExecuteAsync(tab);
        var copy = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && copy.Status == TabStatus.Idle && copy.IsSettled, "the copy");

        Assert.Equal(2, h.Shell.AllTabs.Count());
        var launch = h.Factory.Launches[1];
        Assert.Equal(("s1", true, (string?)null), (launch.Resume, launch.ForkSession, launch.ResumeSessionAt));
        Assert.Equal(["first", "second"], copy.Items.OfType<UserMessageItem>().Select(m => m.Text));
        Assert.Equal("", copy.ComposerText);
    }

    [Fact]
    public async Task Duplicating_a_tab_without_a_session_opens_a_new_one_in_the_folder()
    {
        await using var h = new TabTestHarness();
        // The new tab runs alongside the first.
        h.Factory.ProcessPerSession = true;
        var tab = await h.OpenTabAsync();

        await h.Shell.DuplicateTabCommand.ExecuteAsync(tab);
        var copy = h.Shell.SelectedTab!;
        Assert.NotSame(tab, copy);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2, $"the copy ({h.Factory.Launches.Count} launches, {copy.Status}, {copy.ErrorMessage})");

        Assert.Null(h.Factory.Launches[1].Resume);
        Assert.False(h.Factory.Launches[1].ForkSession);
    }
}
