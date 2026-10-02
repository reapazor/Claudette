using Claudette.App.Conversation;
using Claudette.Core;
using Claudette.Core.ScratchPads;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab's scratch pad (DESIGN.md §18, "Scratch pad"): its project's, the same one every tab in the project shows on
/// the side panel's Scratch Pad page. Text from the conversation goes into it from a message's chip, a code block's
/// header or the menu of selected text, and the page opens on it.
/// </summary>
public sealed partial class TabViewModel
{
    private ScratchPadViewModel? _scratchPad;

    /// <summary>
    /// The project's scratch pad, taken from <see cref="Services.ScratchPadService"/> when first needed: showing the page,
    /// or adding to it. Every open tab has a view, so the page binds to <see cref="ShownScratchPad"/> instead, and a tab
    /// that never uses its pad doesn't read it.
    /// </summary>
    public ScratchPadViewModel ScratchPad
    {
        get
        {
            if (_scratchPad is null)
            {
                _scratchPad = _services.ScratchPads.Acquire(Folder, Formats.FolderName(GroupFolder));
                OnPropertyChanged(nameof(ShownScratchPad));
            }
            return _scratchPad;
        }
    }

    /// <summary>The pad once the tab has used it, for the Scratch Pad page; null before.</summary>
    public ScratchPadViewModel? ShownScratchPad => _scratchPad;

    /// <summary>The tab works in another folder now (a worktree, or one chosen for a missing folder): its project may be another.</summary>
    private void OnScratchPadFolderChanged()
    {
        if (_scratchPad is not { } old)
        {
            return;
        }
        _scratchPad = _services.ScratchPads.Acquire(Folder, Formats.FolderName(GroupFolder));
        _services.ScratchPads.Release(old);
        if (_scratchPad != old)
        {
            OnPropertyChanged(nameof(ScratchPad));
            OnPropertyChanged(nameof(ShownScratchPad));
        }
    }

    private void ReleaseScratchPad()
    {
        if (_scratchPad is { } pad)
        {
            _scratchPad = null;
            _services.ScratchPads.Release(pad);
            OnPropertyChanged(nameof(ShownScratchPad));
        }
    }

    /// <summary>
    /// <b>Add to scratch pad</b>: opens the side panel on the project's pad and adds <paramref name="text"/> at its end,
    /// where the page shows it. Text that's only blank adds nothing.
    /// </summary>
    public void AddToScratchPad(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        // The page first, so it's there to show what's added.
        OpenSidePanelPage(SidePanelPage.ScratchPad);
        if (ScratchPad.Append(text))
        {
            _shell.Announce("Added to the scratch pad.");
        }
    }

    /// <summary>A message's <b>Add to scratch pad</b>: all of it, as <b>Copy message</b> copies it.</summary>
    [RelayCommand]
    private void AddMessageToScratchPad(MessageItem? message)
    {
        if (message is not null)
        {
            AddToScratchPad(message.CopyText);
        }
    }

    /// <summary>A code block's <b>Add to scratch pad</b>: its code, fenced in its language.</summary>
    public void AddCodeToScratchPad(string code, string? language) => AddToScratchPad(ScratchPadText.Fence(code, language));

    /// <summary><b>Copy</b> in the menu of selected text: through the same clipboard as everything else.</summary>
    public Task CopyTextAsync(string text) => _services.Platform.SetClipboardTextAsync(text);
}
