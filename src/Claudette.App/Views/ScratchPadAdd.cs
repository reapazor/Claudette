using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.ViewModels;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;

namespace Claudette.App.Views;

/// <summary>
/// <b>Add to scratch pad</b> in a tab (DESIGN.md §18, "Scratch pad"): the menu of text selected in the conversation, with
/// <b>Copy</b> beside it, and the button in a code block's header. The tab adds the text to its project's pad.
/// </summary>
internal static class ScratchPadAdd
{
    /// <summary>The button a code block's header has (App.axaml), shown in a tab by its styles.</summary>
    public const string CodeBlockButton = "AddToScratchPadButton";

    /// <summary>
    /// Handles the code blocks' button anywhere in <paramref name="tab"/>, whose data context is the tab, and the menu of
    /// selected text in its <paramref name="conversation"/>.
    /// </summary>
    public static void Attach(Control tab, Control conversation)
    {
        tab.AddHandler(Button.ClickEvent, OnClick);
        // What was selected when the right button went down, in case pressing it cleared the selection.
        Selection? pressed = null;
        conversation.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            pressed = null;
            if (e.GetCurrentPoint(conversation).Properties.IsRightButtonPressed && At(e.Source) is { HasText: true } selection)
            {
                pressed = selection;
                // Kept selected for the menu, rather than cleared or moved by the press.
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        // Tunnel: before a text block's own menu.
        conversation.AddHandler(Control.ContextRequestedEvent, (_, e) =>
        {
            var at = At(e.Source);
            if (at is { HasText: false } && pressed is { HasText: true } earlier && earlier.Owner == at.Owner)
            {
                at = earlier;
            }
            pressed = null;
            if (tab.DataContext is TabViewModel viewModel && at is not null && Menu(viewModel, at) is { } menu)
            {
                menu.Open(at.Owner);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// The menu for <paramref name="at"/>: Copy, Quote in reply (DESIGN.md §5, "Copy and times") and Add to scratch pad for
    /// selected text, or for the whole message under the pointer when nothing is selected, with Show as the plan for a
    /// whole reply (DESIGN.md §5, "Tasks"). Null elsewhere, which leaves a control's own menu to it.
    /// </summary>
    internal static ContextMenu? Menu(TabViewModel tab, Selection at)
    {
        if (at.HasText)
        {
            var text = at.Text!;
            return new ContextMenu
            {
                Items =
                {
                    new MenuItem { Header = "Copy", Command = new AsyncRelayCommand(() => tab.CopyTextAsync(text)) },
                    new MenuItem { Header = "Quote in reply", Command = new RelayCommand(() => tab.QuoteInComposer(text)) },
                    new MenuItem { Header = "Add to scratch pad", Command = new RelayCommand(() => tab.AddToScratchPad(text)) },
                },
            };
        }
        if (at.Message is { } message)
        {
            var menu = new ContextMenu
            {
                Items =
                {
                    new MenuItem { Header = "Copy message", Command = tab.CopyMessageCommand, CommandParameter = message },
                    new MenuItem { Header = "Quote message", Command = tab.QuoteMessageCommand, CommandParameter = message },
                    new MenuItem { Header = "Add message to scratch pad", Command = tab.AddMessageToScratchPadCommand, CommandParameter = message },
                },
            };
            if (message is AssistantTextItem { IsStreaming: false } reply)
            {
                menu.Items.Add(new MenuItem { Header = "Show as the plan", Command = tab.ShowAsPlanCommand, CommandParameter = reply });
            }
            return menu;
        }
        return null;
    }

    /// <summary>
    /// What's selected where the pointer is: in a reply, across its Markdown (LiveMarkdown's renderer knows the selection);
    /// elsewhere, in the text block. Null outside any.
    /// </summary>
    internal static Selection? At(object? source)
    {
        if (source is not Visual visual)
        {
            return null;
        }
        var message = visual.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ConversationItem>().FirstOrDefault() as MessageItem;
        if (visual.FindAncestorOfType<MarkdownRenderer>(includeSelf: true) is { } renderer)
        {
            return new Selection(renderer, renderer.SelectedText, message);
        }
        if (visual.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is { } block)
        {
            return new Selection(block, block.SelectedText, message);
        }
        return message is null ? null : new Selection(visual as Control ?? visual.FindAncestorOfType<Control>()!, null, message);
    }

    /// <summary>A code block's <b>Add to scratch pad</b>: its code, fenced in its language.</summary>
    private static void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TabViewModel tab } && e.Source is Button { Name: CodeBlockButton } button && button.TemplatedParent is CodeBlock block)
        {
            e.Handled = true;
            tab.AddCodeToScratchPad(block.Inlines.Text ?? "", block.Language);
        }
    }

    /// <summary>Where the menu opens, what's selected there, and the message it's in.</summary>
    /// <param name="Owner">The reply's renderer or the text block that holds the selection.</param>
    internal sealed record Selection(Control Owner, string? Text, MessageItem? Message)
    {
        public bool HasText => !string.IsNullOrWhiteSpace(Text);
    }
}
