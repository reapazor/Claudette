using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Diffs;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>What each turn changed, on its footer (DESIGN.md §8, "Changes per turn").</summary>
public class ChangesPerTurnTests
{
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"session_id":"s1","duration_ms":1200}""";

    /// <summary>A turn whose Edit changes <paramref name="path"/> from <paramref name="before"/> to <paramref name="after"/>, as Claude Code reports it.</summary>
    private static async Task EditTurnAsync(TabTestHarness h, TabViewModel tab, string id, string path, string before, string after)
    {
        tab.ComposerText = $"change it ({id})";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5" });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "Edit", ["input"] = new JsonObject { ["file_path"] = path, ["old_string"] = "a", ["new_string"] = "b" } }) },
        });
        await File.WriteAllTextAsync(path, after, TestContext.Current.CancellationToken);
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "The file has been updated." }) },
            ["tool_use_result"] = new JsonObject { ["filePath"] = path, ["oldString"] = "a", ["newString"] = "b", ["originalFile"] = before, ["structuredPatch"] = new JsonArray() },
        });
        h.Transport.Emit(Result);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.Working && tab.Items.LastOrDefault() is TurnSummaryItem, "the turn's footer");
    }

    [Fact]
    public async Task A_turns_footer_lists_the_files_it_changed_counted_as_it_left_them()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "auth.cs");

        await EditTurnAsync(h, tab, "e1", path, "one\n", "one\ntwo\n");

        var footer = (TurnSummaryItem)tab.Items.Last();
        Assert.Equal("1 file changed", footer.FilesText);
        var row = Assert.Single(footer.Files);
        Assert.Equal("auth.cs", row.DisplayPath);
        await TabTestHarness.Eventually(() => row.Stats == "+1 −0", "the counts");

        // A turn that changed nothing has no files on its footer.
        tab.ComposerText = "just talk";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn("Nothing to change.");
        await TabTestHarness.Eventually(() => tab.Items.LastOrDefault() is TurnSummaryItem { HasFiles: false }, "the quiet turn");
    }

    [Fact]
    public async Task A_files_diff_runs_from_before_the_turn_to_how_it_left_it()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        var opened = new List<DiffSource>();
        tab.ChangedFiles.DiffRequested += opened.Add;

        await EditTurnAsync(h, tab, "e1", path, "one\n", "one\ntwo\n");
        var first = (TurnSummaryItem)tab.Items.Last();
        await EditTurnAsync(h, tab, "e2", path, "one\ntwo\n", "one\ntwo\nthree\n");
        var second = (TurnSummaryItem)tab.Items.Last();

        // The first turn's changes end where the second turn's start.
        tab.ChangedFiles.OpenTurnDiffCommand.Execute(first.Files[0]);
        var earlier = opened.Last();
        Assert.Equal(("one\n", "one\ntwo\n"), (earlier.Before, earlier.After?.Text));
        Assert.Equal("this turn's changes: before it, against how it left it", earlier.BeforeLabel);
        Assert.False(earlier.AllowRevert);
        Assert.Null(earlier.Review);
        Assert.Null(earlier.OpenInDiffTool);

        // The last turn's are against the file now.
        tab.ChangedFiles.OpenTurnDiffCommand.Execute(second.Files[0]);
        var latest = opened.Last();
        Assert.Equal("one\ntwo\n", latest.Before);
        Assert.Null(latest.After);
        Assert.Equal("this turn's changes: before it, against the file now", latest.BeforeLabel);

        // The diff view shows what it was given, and offers no revert.
        var view = new DiffWindowViewModel(earlier, dark: false);
        await TabTestHarness.Eventually(() => !view.IsLoading, "the diff");
        Assert.Equal("+1 −0", view.Stats);
        Assert.False(view.CanRevert);
    }

    [Fact]
    public async Task A_restored_tabs_earlier_turns_have_no_files_on_their_footers()
    {
        await using var h = new TabTestHarness();
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        await File.WriteAllTextAsync(path, "one\ntwo\n", TestContext.Current.CancellationToken);
        h.WriteTranscript("s1",
            Wire.Entry("assistant", "2026-09-28T11:00:00Z", Wire.Message(new JsonObject { ["type"] = "tool_use", ["id"] = "e1", ["name"] = "Edit", ["input"] = new JsonObject { ["file_path"] = path } })),
            Wire.Entry("user", "2026-09-28T11:00:01Z", Wire.ResultMessage("e1", "The file has been updated."),
                new JsonObject { ["filePath"] = path, ["oldString"] = "a", ["originalFile"] = "one\n", ["structuredPatch"] = new JsonArray() }));
        h.Services.State.Tabs = [new Core.Settings.TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "s1" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();
        h.Shell.SelectedTab = tab;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the restored tab");
        Assert.Single(tab.ChangedFiles.Files);

        // The first live turn shows only its own changes.
        tab.ComposerText = "just talk";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn("Nothing to change.");
        await TabTestHarness.Eventually(() => tab.Items.LastOrDefault() is TurnSummaryItem, "the live turn");
        Assert.False(((TurnSummaryItem)tab.Items.Last()).HasFiles);
    }
}
