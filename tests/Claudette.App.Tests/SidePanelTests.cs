using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Platform.Processes;

namespace Claudette.App.Tests;

/// <summary>The side panel: changed files (DESIGN.md §8) and the process monitor (DESIGN.md §4).</summary>
public class SidePanelTests
{
    [Fact]
    public async Task A_new_file_from_Write_shows_as_added()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "notes.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\n", TestContext.Current.CancellationToken);

        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "w1", ["name"] = "Write", ["input"] = new JsonObject { ["file_path"] = path, ["content"] = "one\ntwo\n" } }) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "w1", ["content"] = "File created" }) },
            ["tool_use_result"] = new JsonObject { ["type"] = "create", ["filePath"] = path, ["content"] = "one\ntwo\n", ["originalFile"] = null },
        });

        await TabTestHarness.Eventually(() => tab.ChangedFiles.Count == 1, "the changed file");
        var row = tab.ChangedFiles[0];
        Assert.Equal("A", row.Status);
        Assert.Equal("notes.txt", row.DisplayPath);
        Assert.Equal("+2 −0", row.Stats);
        Assert.Equal("1 file changed", tab.ChangedFilesSummary);
    }

    [Fact]
    public async Task Opening_a_changed_file_asks_for_the_diff_view()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "a.cs");
        await File.WriteAllTextAsync(path, "new\n", TestContext.Current.CancellationToken);
        var row = new ChangedFileRow { Path = path, DisplayPath = "a.cs", Status = "M", StatusText = "Modified", Before = "old\n" };
        Diffs.DiffSource? requested = null;
        tab.DiffRequested += source => requested = source;

        await tab.OpenFileCommand.ExecuteAsync(row);

        Assert.NotNull(requested);
        Assert.Equal("old\n", requested.Before);
        Assert.Null(requested.OpenInDiffTool);
    }

    [Fact]
    public async Task The_process_summary_counts_what_the_tab_started()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.Add((5001, "node"));

        h.Time.Advance(ProcessSampler.SummaryInterval);

        await TabTestHarness.Eventually(() => tab.ProcessSummaryText is not null, "a sample");
        Assert.StartsWith("1 proc · ", tab.ProcessSummaryText, StringComparison.Ordinal);
        Assert.True(tab.HasBusyProcesses);
        Assert.Equal(["claude", "node"], tab.Processes.Select(p => p.Name));
    }

    [Fact]
    public async Task A_background_task_is_stopped_through_Claude_Code()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        h.Transport.Emit("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"npm run dev","run_in_background":true}}]}}""");
        h.Transport.Emit("""{"type":"system","subtype":"task_started","task_id":"task-9","tool_use_id":"b1","description":"npm run dev","task_type":"local_bash"}""");
        await TabTestHarness.Eventually(() => tab.Items.OfType<Conversation.ToolUseItem>().Any(), "the Bash card");
        h.Trees.Trees[4242].Children.Add((6001, "node"));

        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.Processes.Any(p => p.Name == "node"), "the process");
        var row = tab.Processes.Single(p => p.Name == "node");
        Assert.Equal("Bash: npm run dev", row.ToolText);

        tab.StopProcessCommand.Execute(row);
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => h.Transport.SentControlSubtypes.Contains("stop_task"), "stop_task");
        Assert.Empty(h.Trees.Trees[4242].Stopped);
    }

    [Fact]
    public async Task Closing_a_tab_stops_its_processes_unless_asked_to_leave_them()
    {
        await using var h = new TabTestHarness();
        var first = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.Add((5001, "vite"));

        h.Shell.CloseTabCommand.Execute(first);

        var confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Contains("vite (5001)", confirmation.Message, StringComparison.Ordinal);
        Assert.Equal("Close, leave running", confirmation.SecondaryText);

        await confirmation.SecondaryCommand.ExecuteAsync(null);

        Assert.False(tree.Killed);
        Assert.Empty(h.Shell.AllTabs);
    }

    [Fact]
    public async Task Closing_a_tab_normally_stops_everything_it_started()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.Add((5001, "vite"));

        h.Shell.CloseTabCommand.Execute(tab);
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);

        Assert.True(tree.Killed);
    }
}
