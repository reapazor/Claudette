using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.Core.Composer;

namespace Claudette.App.Views;

/// <summary>The composer's <c>/</c> and <c>@</c> autocomplete, and attaching by drop, paste and picker (DESIGN.md §5).</summary>
public partial class ComposerView
{
    private bool _completionUpdateQueued;
    private bool _pasteTextThrough;

    private void WireComposerAssist()
    {
        // Text and caret both change on a keystroke; one update after both is enough.
        Composer.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty)
            {
                QueueCompletionUpdate();
            }
        };
        Composer.LostFocus += (_, _) =>
        {
            if (CompletionPopup.Child?.IsPointerOver != true)
            {
                ViewModel?.Completions.Close();
            }
        };
        // Switching tabs leaves no popup open to reappear when coming back.
        TabViewModel? shown = null;
        DataContextChanged += (_, _) =>
        {
            shown?.Completions.Close();
            shown = ViewModel;
        };
        Composer.PastingFromClipboard += OnComposerPasting;
        ComposerBox.AddHandler(DragDrop.DragEnterEvent, OnComposerDragOver, handledEventsToo: true);
        ComposerBox.AddHandler(DragDrop.DragOverEvent, OnComposerDragOver, handledEventsToo: true);
        ComposerBox.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ComposerBox.Classes.Set("dropping", false), handledEventsToo: true);
        ComposerBox.AddHandler(DragDrop.DropEvent, OnComposerDrop, handledEventsToo: true);
    }

    private void QueueCompletionUpdate()
    {
        if (_completionUpdateQueued)
        {
            return;
        }
        _completionUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _completionUpdateQueued = false;
            if (ViewModel is { } tab && Composer.IsFocused)
            {
                tab.Completions.Update(Composer.Text ?? "", Composer.CaretIndex);
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// While the popup is open: Up and Down move, Enter and Tab pick, Esc closes. Enter with nothing to pick, or on what's
    /// already typed in full, still sends, so a command Claudette doesn't list can be sent as typed.
    /// </summary>
    private bool HandleCompletionKey(KeyEventArgs e)
    {
        if (ViewModel is not { Completions: { IsOpen: true } completions } || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return false;
        }
        switch (e.Key)
        {
            case Key.Down or Key.Up when e.KeyModifiers == KeyModifiers.None:
                completions.MoveSelection(e.Key == Key.Down ? 1 : -1);
                if (completions.SelectedIndex >= 0 && CompletionList.ContainerFromIndex(completions.SelectedIndex) is { } container)
                {
                    container.BringIntoView();
                }
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None && completions.IsTypedInFull:
                completions.Close();
                return false;
            case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None && completions.SelectedItem is not null:
                ApplyEdit(completions.Accept());
                e.Handled = true;
                break;
            case Key.Escape:
                completions.Dismiss();
                e.Handled = true;
                break;
        }
        return e.Handled;
    }

    private void OnCompletionClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CompletionItem item && ViewModel is { } tab)
        {
            ApplyEdit(tab.Completions.Accept(item));
            Composer.Focus();
        }
    }

    private void ApplyEdit(ComposerEdit? edit)
    {
        if (edit is { } change)
        {
            Composer.Text = change.Text;
            Composer.CaretIndex = change.Caret;
            Composer.ClearSelection();
        }
    }

    /// <summary>Inserts mentions at the caret, with a space before them when the caret is after a word.</summary>
    private void InsertAtCaret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var current = Composer.Text ?? "";
        var caret = Math.Clamp(Composer.CaretIndex, 0, current.Length);
        var gap = caret > 0 && !char.IsWhiteSpace(current[caret - 1]) ? " " : "";
        Composer.Text = current.Insert(caret, gap + text);
        Composer.CaretIndex = caret + gap.Length + text.Length;
        Composer.Focus();
    }

    /// <summary>
    /// Copied files and images are attached (DESIGN.md §5, "Attachments"); text pastes as usual. Deciding needs the
    /// clipboard, which is asynchronous, so the paste is held and redone for text.
    /// </summary>
    private void OnComposerPasting(object? sender, RoutedEventArgs e)
    {
        if (_pasteTextThrough)
        {
            _pasteTextThrough = false;
            return;
        }
        if (ViewModel is not { } tab)
        {
            return;
        }
        e.Handled = true;
        _ = PasteAsync(tab);
    }

    private async Task PasteAsync(TabViewModel tab)
    {
        string? insert;
        try
        {
            insert = await tab.PasteAttachmentsAsync();
        }
        catch (Exception)
        {
            insert = null; // Couldn't read the clipboard's files or image; paste it as text.
        }
        if (insert is null)
        {
            _pasteTextThrough = true;
            Composer.Paste();
            _pasteTextThrough = false;
        }
        else
        {
            InsertAtCaret(insert);
        }
    }

    private async void OnAttachClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } tab)
        {
            InsertAtCaret(await tab.PickAttachmentAsync());
        }
    }

    private void OnComposerDragOver(object? sender, DragEventArgs e)
    {
        var accepts = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Bitmap);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        ComposerBox.Classes.Set("dropping", accepts);
        e.Handled = true;
    }

    private async void OnComposerDrop(object? sender, DragEventArgs e)
    {
        ComposerBox.Classes.Set("dropping", false);
        if (ViewModel is not { } tab)
        {
            return;
        }
        e.Handled = true;
        var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
        {
            InsertAtCaret(await tab.AddFilesAsync(paths));
        }
        else if (e.DataTransfer.TryGetBitmap() is { } bitmap)
        {
            using (bitmap)
            {
                tab.AddImage(ImageFiles.Encode(bitmap), "Dropped image");
            }
        }
    }
}
