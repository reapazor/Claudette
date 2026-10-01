using Claudette.App.Conversation;
using Claudette.App.Tests.Support;

namespace Claudette.App.Tests;

/// <summary><b>Export conversation…</b> in the tab's menu (DESIGN.md §5, "Export").</summary>
public sealed class ExportCommandTests
{
    [Fact]
    public async Task Export_saves_Markdown_or_a_web_page_by_the_chosen_name()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "Fix the build";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn("Fixed it.");
        await TabTestHarness.Eventually(() => tab.Status == ViewModels.TabStatus.Idle && tab.IsSettled, "the turn");
        var markdown = Path.Combine(h.Root, "out.md");
        h.Platform.SavePathToPick = markdown;

        await tab.ExportConversationCommand.ExecuteAsync(null);

        Assert.Equal($"{tab.DisplayName}.md", h.Platform.LastSaveDialog!.Value.Name);
        Assert.Equal(["md", "html"], h.Platform.LastSaveDialog.Value.Types.Select(t => t.Extension));
        var text = await File.ReadAllTextAsync(markdown, TestContext.Current.CancellationToken);
        Assert.Contains("Fix the build", text, StringComparison.Ordinal);
        Assert.Contains("Fixed it.", text, StringComparison.Ordinal);
        Assert.Contains(tab.Items, i => i is NoteItem { Text: var note } && note == $"Exported the conversation to {markdown}.");

        var page = Path.Combine(h.Root, "out.html");
        h.Platform.SavePathToPick = page;
        await tab.ExportConversationCommand.ExecuteAsync(null);
        Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(page, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_dialog_writes_nothing()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var before = tab.Items.Count;

        await tab.ExportConversationCommand.ExecuteAsync(null);

        Assert.Equal(before, tab.Items.Count);
    }
}
