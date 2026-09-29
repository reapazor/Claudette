using Avalonia.Controls;
using Avalonia.Interactivity;
using Claudette.App.ViewModels;
using LiveMarkdown.Avalonia;

namespace Claudette.App.Views;

/// <summary>
/// <b>Copy</b> on a code block in Markdown (DESIGN.md §5, "Copy and times"). LiveMarkdown's button raises
/// <see cref="CodeBlock.CopyingToClipboardEvent"/> first; the tab takes it from there, so the code goes through
/// <c>IPlatformServices</c> and the block says "Copied" for a moment (its <c>copied</c> class, styled in App.axaml).
/// </summary>
internal static class CodeBlockCopy
{
    /// <summary>Handles Copy for the code blocks in <paramref name="host"/>, whose data context is the tab.</summary>
    public static void Attach(Control host) => host.AddHandler(CodeBlock.CopyingToClipboardEvent, OnCopying);

    private static async void OnCopying(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TabViewModel tab } || e.Source is not CodeBlock block)
        {
            return;
        }
        // Handled before the first await: LiveMarkdown checks as soon as the event returns, and then doesn't copy itself.
        e.Handled = true;
        try
        {
            await tab.CopyCodeAsync(block.Inlines.Text ?? "", block, copied => block.Classes.Set("copied", copied));
        }
        catch (Exception)
        {
            // The clipboard can be unavailable for a moment; the button just doesn't say "Copied".
        }
    }
}
