using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;
using LiveMarkdown.Avalonia;

namespace Claudette.App.UiTests;

/// <summary>
/// The scratch pad rendered (DESIGN.md §18, "Scratch pad"): its page, the button on a code block, and the menu of text
/// selected in the conversation.
/// </summary>
public class ScratchPadUiTests
{
    [AvaloniaFact]
    public async Task The_page_shows_the_projects_pad_and_selects_what_was_added_without_taking_the_keyboard()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        // A tab that hasn't used its pad hasn't read it.
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ScratchPadView>(), v => v.DataContext is not null);

        tab.AddToScratchPad("Check the retry limit");
        tab.AddToScratchPad("And the timeout");
        UiText.Settle(window);

        var page = window.GetVisualDescendants().OfType<ScratchPadView>().Single(v => v.IsEffectivelyVisible);
        var editor = page.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Editor");
        await UiText.SettleUntilAsync(window, () => editor.SelectedText == "And the timeout", "what was added, selected");
        Assert.Equal("Check the retry limit\n\nAnd the timeout\n", editor.Text);
        Assert.False(editor.IsFocused);
        Assert.Equal("Scratch pad", AutomationProperties.GetName(editor));
        var shown = UiText.Describe(page);

        // What's typed there is the project's pad.
        editor.Text = "Edited here";
        Assert.Equal("Edited here", tab.ScratchPad.Text);

        await Verify(shown);
    }

    [AvaloniaFact]
    public async Task A_code_blocks_button_adds_its_code_in_a_tab_but_isnt_there_outside_one()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        h.Transport.EmitTurn("Try this:\n\n```csharp\nretry(3);\n```\n");
        await UiText.SettleUntilAsync(window, () => window.GetVisualDescendants().OfType<CodeBlock>().Any(), "the code block");
        var button = AddButton(window.GetVisualDescendants().OfType<CodeBlock>().Single());

        Assert.True(button.IsEffectivelyVisible);
        Assert.Equal("Add to scratch pad", AutomationProperties.GetName(button));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UiText.Settle(window);

        Assert.Equal("```csharp\nretry(3);\n```\n", tab.ScratchPad.Text);
        Assert.True(tab.IsSidePanelOpen && tab.IsScratchPadPage);

        // Outside a tab, as in the agent map's own window, there's no pad to add to.
        var markdown = new ObservableStringBuilder();
        markdown.Append("```csharp\nretry(3);\n```\n");
        var elsewhere = UiText.Show(new MarkdownRenderer { MarkdownBuilder = markdown }, 600, 300);
        await UiText.SettleUntilAsync(elsewhere, () => elsewhere.GetVisualDescendants().OfType<CodeBlock>().Any(), "the code block elsewhere");
        Assert.False(AddButton(elsewhere.GetVisualDescendants().OfType<CodeBlock>().Single()).IsVisible);
        elsewhere.Close();
    }

    [AvaloniaFact]
    public async Task Text_selected_in_a_reply_has_Copy_and_Add_to_scratch_pad_on_its_menu()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        h.Transport.EmitTurn("Use exponential backoff for retries.");
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        await UiText.SettleUntilAsync(window, () => Reply(view)?.GetVisualDescendants().OfType<MarkdownTextBlock>().Any() == true, "the reply");
        var renderer = Reply(view)!;
        var block = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().First();
        renderer.SelectAll();

        // A right click keeps the selection, for the menu.
        var at = block.TranslatePoint(new Point(block.Bounds.Width / 2, block.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        UiText.Settle(window);
        Assert.Equal("Use exponential backoff for retries.", renderer.SelectedText?.Trim());

        var menu = ScratchPadAdd.Menu(tab, ScratchPadAdd.At(block)!)!;
        Assert.Equal(["Copy", "Add to scratch pad"], menu.Items.OfType<MenuItem>().Select(m => m.Header as string));
        menu.Items.OfType<MenuItem>().First().Command!.Execute(null);
        await UiText.SettleUntilAsync(window, () => h.Platform.Clipboard?.Trim() == "Use exponential backoff for retries.", "the copy");
        menu.Items.OfType<MenuItem>().Last().Command!.Execute(null);
        Assert.Equal("Use exponential backoff for retries.\n", tab.ScratchPad.Text);
        Assert.True(tab.IsScratchPadPage);

        // The menu key, or a right click, opens it in place of the text block's own.
        var request = new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent };
        block.RaiseEvent(request);
        Assert.True(request.Handled);

        // With nothing selected, it's about the whole message.
        renderer.ClearSelection();
        var whole = ScratchPadAdd.Menu(tab, ScratchPadAdd.At(block)!)!;
        Assert.Equal(["Copy message", "Add message to scratch pad"], whole.Items.OfType<MenuItem>().Select(m => m.Header as string));
    }

    private static Button AddButton(CodeBlock block) =>
        block.GetVisualDescendants().OfType<Button>().Single(b => b.Name == ScratchPadAdd.CodeBlockButton);

    private static MarkdownRenderer? Reply(TabView view) =>
        view.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "ConversationItems").GetVisualDescendants().OfType<MarkdownRenderer>().FirstOrDefault();
}
