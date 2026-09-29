using System.Collections.ObjectModel;
using Claudette.Core.Composer;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>An image attached in the composer, shown as a removable thumbnail (DESIGN.md §5, "Attachments").</summary>
public sealed class ComposerAttachment(MessageImage image, string name)
{
    public MessageImage Image { get; } = image;

    public string Name { get; } = name;

    public byte[] Data => Image.Data;
}

/// <summary>The composer's autocomplete and attachments (DESIGN.md §5, "Composer").</summary>
public sealed partial class TabViewModel
{
    private SlashCommandCatalog? _slashCommands;
    private InitializeResult? _slashCommandsFrom;
    private ProjectFileIndex? _fileIndex;
    private ComposerCompletions? _completions;

    /// <summary>The <c>/</c> and <c>@</c> popup.</summary>
    public ComposerCompletions Completions => _completions ??= new ComposerCompletions(() => SlashCommands, () => FileIndex, _services.Dispatcher);

    /// <summary>The session's slash commands: from <c>initialize</c>, then each turn's <c>system/init</c>.</summary>
    internal SlashCommandCatalog SlashCommands
    {
        get
        {
            _slashCommands ??= new SlashCommandCatalog();
            if (_session?.Initialization is { } initialization && !ReferenceEquals(initialization, _slashCommandsFrom))
            {
                _slashCommandsFrom = initialization;
                _slashCommands.SetDescribed(initialization.Commands);
            }
            return _slashCommands;
        }
    }

    /// <summary>The working folder's files, for <c>@</c>. Made again if the tab moves to another folder.</summary>
    internal ProjectFileIndex FileIndex =>
        _fileIndex is { } index && index.Folder == Folder ? index : _fileIndex = new ProjectFileIndex(Folder, _services.Git, _services.Time);

    /// <summary>Keeps the command list and file index current from the session's events.</summary>
    private void ObserveForComposer(SessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case TurnStarted started:
                SlashCommands.SetAccepted(started.Init);
                break;
            case SystemNotice { Message.Subtype: "commands_changed" } changed:
                // The full new list. Reading SlashCommands first takes initialize's list, which this then replaces.
                SlashCommands.SetDescribed(SlashCommandInfo.ParseList(changed.Message.Raw.GetArray("commands")));
                break;
            case TurnCompleted:
                // The turn may have added or removed files.
                _fileIndex?.Invalidate();
                break;
        }
    }

    // ---- Attachments (DESIGN.md §5, "Attachments") ---------------------------------------------------------

    public ObservableCollection<ComposerAttachment> Attachments { get; } = [];

    /// <summary>Why the last dropped or pasted file wasn't attached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachmentError))]
    public partial string? AttachmentError { get; set; }

    public bool HasAttachmentError => !string.IsNullOrEmpty(AttachmentError);

    [RelayCommand]
    private void RemoveAttachment(ComposerAttachment? attachment)
    {
        if (attachment is not null && Attachments.Remove(attachment))
        {
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void DismissAttachmentError() => AttachmentError = null;

    /// <summary>
    /// Attaches dropped or pasted files: images as thumbnails, other files and folders as <c>@path</c> mentions.
    /// Returns the mentions for the view to insert at the caret, or null when there are none.
    /// </summary>
    public async Task<string?> AddFilesAsync(IReadOnlyList<string> paths)
    {
        var mentions = new List<string>();
        var errors = new List<string>();
        foreach (var path in paths)
        {
            var attachment = await Core.Composer.Attachments.FromPathAsync(path, Folder);
            if (attachment.Image is { } image)
            {
                if (TryAttach(image, attachment.Name) is { } error)
                {
                    errors.Add(error);
                }
            }
            else if (attachment.Mention is { } mention)
            {
                mentions.Add(mention);
            }
            else if (attachment.Error is { } error)
            {
                errors.Add(error);
            }
        }
        AttachmentError = errors.Count > 0 ? string.Join(" ", errors) : null;
        return mentions.Count > 0 ? string.Join(" ", mentions) + " " : null;
    }

    /// <summary>Attaches an image from the clipboard or a drag, given as bytes. False when it can't be.</summary>
    public bool AddImage(byte[] data, string name)
    {
        var attachment = Core.Composer.Attachments.FromImageBytes(data, name);
        var error = attachment.Image is { } image ? TryAttach(image, name) : attachment.Error;
        AttachmentError = error;
        return error is null;
    }

    /// <summary>
    /// A paste into the composer: copied files are attached, then text is left to paste as usual, then an image (such
    /// as a screenshot) is attached. Text wins over an image because apps often put both, as an image of the same
    /// text. Returns what to insert (mentions, or nothing), or null to let the text box paste the text itself.
    /// </summary>
    public async Task<string?> PasteAttachmentsAsync()
    {
        var platform = _services.Platform;
        if (await platform.GetClipboardFilesAsync() is { Count: > 0 } files)
        {
            return await AddFilesAsync(files) ?? "";
        }
        if (await platform.ClipboardHasTextAsync())
        {
            return null;
        }
        if (await platform.GetClipboardImageAsync() is { } image)
        {
            AddImage(image, "Pasted image");
            return "";
        }
        return null;
    }

    /// <summary>The composer's attach button: a file picker. Returns a mention to insert, as <see cref="AddFilesAsync"/>.</summary>
    public async Task<string?> PickAttachmentAsync() =>
        await _services.Platform.PickFileAsync("Attach a file") is { } path ? await AddFilesAsync([path]) : null;

    /// <returns>Why it wasn't attached, or null.</returns>
    private string? TryAttach(MessageImage image, string name)
    {
        if (Attachments.Count >= Core.Composer.Attachments.MaxImagesPerMessage)
        {
            return $"A message can have up to {Core.Composer.Attachments.MaxImagesPerMessage} images.";
        }
        Attachments.Add(new ComposerAttachment(image, name));
        SendCommand.NotifyCanExecuteChanged();
        return null;
    }

    /// <summary>Takes the attached images for a message being sent, and clears them from the composer.</summary>
    private IReadOnlyList<MessageImage> TakeAttachments()
    {
        var images = Attachments.Select(a => a.Image).ToArray();
        Attachments.Clear();
        AttachmentError = null;
        return images;
    }
}
