using Claudette.App.Conversation;
using Claudette.Core;
using Claudette.Core.Composer;
using Claudette.Core.Development;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A message in the stash, as the composer's <b>Stashed</b> menu lists it (DESIGN.md §5, "Drafts and the stash").</summary>
/// <param name="Title">Its first line, or what's attached when it has no text.</param>
/// <param name="Detail">The folder it was written in, and when it was stashed.</param>
public sealed record StashEntry(StashedDraft Stashed, string Title, string Detail);

/// <summary>
/// The tab's unsent message on disk, the stash and quoting (DESIGN.md §5, "Drafts and the stash"): the draft is saved a
/// moment after it changes, so it outlives quitting; <b>Stash</b> puts it aside for any tab; <b>Quote in reply</b> brings
/// text from the conversation into it.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>How long the draft waits after a change before it's written, so typing writes it once it pauses.</summary>
    internal static readonly TimeSpan DraftSaveDelay = TimeSpan.FromSeconds(1);

    /// <summary>How much of a stashed message's first line its menu entry shows.</summary>
    private const int StashTitleLength = 60;

    private UiTimeout DraftSave => field ??= new(_services.Time, _services.Dispatcher);

    /// <summary>The composer changed: the draft is written once it settles.</summary>
    private void DraftChanged()
    {
        if (!_closing.IsCancellationRequested)
        {
            DraftSave.Restart(DraftSaveDelay, () => _ = SaveDraftNowAsync());
        }
    }

    /// <summary>Writes the draft now, or deletes it when the composer is empty.</summary>
    internal Task SaveDraftNowAsync()
    {
        DraftSave.Cancel();
        return _services.Drafts.SaveAsync(Id, Draft);
    }

    /// <summary>The tab is closing with a draft not yet written: it's written now, so quitting keeps it.</summary>
    private void FlushDraft()
    {
        if (DraftSave.IsPending)
        {
            _ = SaveDraftNowAsync();
        }
    }

    /// <summary>Puts back the draft saved the last time Claudette ran, if the tab had one.</summary>
    internal void RestoreSavedDraft()
    {
        if (_services.Drafts.Load(Id) is { } draft)
        {
            RestoreDraft(draft);
            DraftSave.Cancel();
        }
    }

    // ---- The stash ------------------------------------------------------------------------------------------------

    /// <summary>The stash, newest first, which every tab shares.</summary>
    public IReadOnlyList<StashEntry> StashEntries => [.. _services.Drafts.Stash.Select(ToEntry)];

    public bool HasStash => _services.Drafts.Stash.Count > 0;

    /// <summary>The composer bar's button: <c>Stashed (2)</c>.</summary>
    public string StashButtonText => $"Stashed ({_services.Drafts.Stash.Count}) ▾";

    private void OnStashChanged() => RefreshStash();

    /// <summary>The stash's entries say how long ago each was stashed: brought up to date as its menu opens.</summary>
    internal void RefreshStash()
    {
        OnPropertyChanged(nameof(StashEntries));
        OnPropertyChanged(nameof(HasStash));
        OnPropertyChanged(nameof(StashButtonText));
    }

    private StashEntry ToEntry(StashedDraft stashed)
    {
        var draft = stashed.Draft;
        var line = draft.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        var title = line is null ? Attached(draft) : line.Length > StashTitleLength ? line[..(StashTitleLength - 1)].TrimEnd() + "…" : line;
        var ago = Formats.Ago(_services.Time.GetUtcNow() - stashed.StashedAt);
        return new StashEntry(stashed, title, $"{Formats.FolderName(stashed.Folder)} · {ago}");

        static string Attached(TabDraft draft) => (draft.Images?.Count ?? 0, draft.PastedTexts?.Count ?? 0) switch
        {
            (0, 0) => "Quick suffixes only",
            (var images, 0) => images == 1 ? "An image" : $"{images} images",
            (0, var pastes) => pastes == 1 ? "Pasted text" : $"{pastes} pasted texts",
            _ => "Images and pasted text",
        };
    }

    /// <summary>
    /// <b>Stash the message</b> (<c>Ctrl/Cmd+S</c>): the whole draft (text, one-off suffixes, images and pastes) leaves the
    /// composer for the stash, where any tab can take it back. Nothing happens with nothing typed.
    /// </summary>
    [RelayCommand]
    private void Stash()
    {
        if (Draft is not { IsEmpty: false } draft)
        {
            return;
        }
        _services.Drafts.AddToStash(new StashedDraft(Guid.NewGuid().ToString("N"), _services.Time.GetUtcNow(), Folder, draft));
        ComposerText = "";
        foreach (var chip in Chips.Where(c => !c.IsKept).ToArray())
        {
            Chips.Remove(chip);
        }
        Attachments.Clear();
        PastedTexts.Clear();
        AttachmentError = null;
        SendCommand.NotifyCanExecuteChanged();
        _shell.Announce("Stashed the message.");
    }

    /// <summary>
    /// A stashed message picked from the menu: it leaves the stash and comes back to the composer, ahead of anything
    /// typed since, as a message taken back does.
    /// </summary>
    [RelayCommand]
    private void Unstash(StashEntry? entry)
    {
        if (entry is null || !_services.Drafts.RemoveFromStash(entry.Stashed.Id))
        {
            return;
        }
        var draft = entry.Stashed.Draft;
        var typed = ComposerText.Trim();
        ComposerText = typed.Length == 0 ? draft.Text : draft.Text.Trim().Length == 0 ? ComposerText : $"{draft.Text.TrimEnd()}\n\n{typed}";
        foreach (var id in draft.SuffixIds)
        {
            AddSuffix(_services.Settings.QuickSuffixes.FirstOrDefault(s => s.Id == id));
        }
        RestoreDraftImages(draft.Images);
        foreach (var paste in draft.PastedTexts ?? [])
        {
            AddPastedText(paste);
        }
        SendCommand.NotifyCanExecuteChanged();
        ComposerFocusRequested?.Invoke();
    }

    /// <summary>× on a stashed message: gone from the stash, in every tab.</summary>
    [RelayCommand]
    private void RemoveStashed(StashEntry? entry)
    {
        if (entry is not null)
        {
            _services.Drafts.RemoveFromStash(entry.Stashed.Id);
        }
    }

    // ---- Quoting (DESIGN.md §5, "Copy and times") ---------------------------------------------------------------

    /// <summary>
    /// <b>Quote in reply</b>: <paramref name="text"/> as a Markdown quote at the end of the composer, with a line after it
    /// to write under, and the focus there.
    /// </summary>
    public void QuoteInComposer(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').Split('\n');
        if (lines.All(l => l.Trim().Length == 0))
        {
            return;
        }
        var quote = string.Join("\n", lines.Select(l => l.Length == 0 ? ">" : $"> {l}"));
        var typed = ComposerText.TrimEnd();
        ComposerText = typed.Length == 0 ? $"{quote}\n\n" : $"{typed}\n\n{quote}\n\n";
        ComposerFocusRequested?.Invoke();
    }

    /// <summary>A message's <b>Quote message</b>: all of it, as <b>Copy message</b> copies it.</summary>
    [RelayCommand]
    private void QuoteMessage(MessageItem? message)
    {
        if (message is not null)
        {
            QuoteInComposer(message.CopyText);
        }
    }
}
