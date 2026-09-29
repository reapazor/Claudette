using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Composer;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

public enum CompletionKind
{
    None,
    SlashCommand,
    File,
}

/// <summary>An entry of the composer's autocomplete popup: a slash command, or a file or folder.</summary>
public sealed partial class CompletionItem : ObservableObject
{
    /// <summary>The command name, or the path relative to the working folder (folders end in <c>/</c>).</summary>
    public required string Value { get; init; }

    /// <summary><c>/compact</c>, or a file's name.</summary>
    public required string Title { get; init; }

    /// <summary>A command's description, or the folder a file is in.</summary>
    public string? Detail { get; init; }

    /// <summary>A command's arguments, such as <c>[name]</c>.</summary>
    public string? Hint { get; init; }

    public bool IsFolder { get; init; }

    public string IconKey { get; init; } = Conversation.ToolIcons.Default;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// The composer's autocomplete (DESIGN.md §5, "Composer"): <c>/</c> at the start of a message lists slash commands,
/// and <c>@</c> lists the working folder's files and folders, filtered as you type. Keyboard first: the view passes
/// the text and caret on every change, and arrow keys, Enter, Tab and Esc while it's open.
/// </summary>
public sealed partial class ComposerCompletions : ObservableObject
{
    private const int MaxItems = 50;

    private readonly Func<SlashCommandCatalog> _commands;
    private readonly Func<ProjectFileIndex> _files;
    private readonly IUiDispatcher _dispatcher;
    private string _text = "";
    private ComposerToken? _token;
    private (CompletionKind Kind, int Start)? _dismissed;
    private int _version;
    private string? _shownQuery;

    public ComposerCompletions(Func<SlashCommandCatalog> commands, Func<ProjectFileIndex> files, IUiDispatcher dispatcher)
    {
        _commands = commands;
        _files = files;
        _dispatcher = dispatcher;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial CompletionKind Kind { get; set; }

    public string Header => Kind == CompletionKind.SlashCommand ? "Commands" : "Files and folders";

    public ObservableCollection<CompletionItem> Items { get; } = [];

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    partial void OnSelectedIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Items.Count)
        {
            Items[oldValue].IsSelected = false;
        }
        if (newValue >= 0 && newValue < Items.Count)
        {
            Items[newValue].IsSelected = true;
        }
    }

    /// <summary>Shown instead of items: nothing matches, or the list is still loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public CompletionItem? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    /// <summary>
    /// What's typed is already the highlighted entry, such as <c>/compact</c> in full: Enter sends the message rather
    /// than completing it again.
    /// </summary>
    public bool IsTypedInFull => SelectedItem is { } item && _token is { } token && string.Equals(item.Value, token.Query, StringComparison.OrdinalIgnoreCase);

    /// <summary>The composer's text or caret changed: opens, filters or closes the popup.</summary>
    public void Update(string text, int caret)
    {
        _text = text;
        var slash = ComposerTokens.FindSlashCommand(text, caret);
        var mention = slash is null ? ComposerTokens.FindMention(text, caret) : null;
        var kind = slash is not null ? CompletionKind.SlashCommand : mention is not null ? CompletionKind.File : CompletionKind.None;
        if ((slash ?? mention) is not { } token)
        {
            _dismissed = null;
            Close();
            return;
        }
        // Esc closes it until the caret leaves that command or mention.
        if (_dismissed is { } dismissed && dismissed.Kind == kind && dismissed.Start == token.Start)
        {
            Close();
            return;
        }
        _dismissed = null;
        _token = token;
        Kind = kind;
        if (kind == CompletionKind.SlashCommand)
        {
            ShowCommands(token.Query);
        }
        else
        {
            _ = ShowFilesAsync(token.Query);
        }
    }

    /// <summary>Up and Down: moves the highlight, wrapping around.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }
        SelectedIndex = ((SelectedIndex < 0 ? (delta > 0 ? -1 : 0) : SelectedIndex) + delta + Items.Count) % Items.Count;
    }

    /// <summary>
    /// Enter, Tab or a click: the composer's text with the picked command or path in place of what was typed. Null when
    /// nothing is highlighted.
    /// </summary>
    public ComposerEdit? Accept(CompletionItem? item = null)
    {
        item ??= SelectedItem;
        if (item is null || _token is not { } token)
        {
            return null;
        }
        var edit = Kind == CompletionKind.SlashCommand
            ? ComposerTokens.ReplaceSlashCommand(_text, token, item.Value)
            : ComposerTokens.ReplaceMention(_text, token, item.Value);
        Close();
        return edit;
    }

    /// <summary>Esc: closes the popup, and keeps it closed while the caret stays in the same command or mention.</summary>
    public void Dismiss()
    {
        if (_token is { } token && IsOpen)
        {
            _dismissed = (Kind, token.Start);
        }
        Close();
    }

    public void Close()
    {
        _version++;
        _shownQuery = null;
        _token = null;
        IsOpen = false;
        Kind = CompletionKind.None;
        Items.Clear();
        SelectedIndex = -1;
        Message = null;
    }

    private void ShowCommands(string query)
    {
        _version++;
        var catalog = _commands();
        var commands = catalog.Filter(query, MaxItems);
        Show(commands.Select(c => new CompletionItem
        {
            Value = c.Name,
            Title = "/" + c.Name,
            Detail = c.Description,
            Hint = string.IsNullOrWhiteSpace(c.ArgumentHint) ? null : c.ArgumentHint,
            IconKey = "IconToolSkill",
        }).ToArray(), catalog.Commands.Count == 0 ? "Claude Code hasn't listed its commands yet." : "No matching commands", query);
    }

    /// <summary>
    /// Filters off the UI thread. A stale listing is used straight away and refreshed behind it, so the popup never
    /// waits on git once the tab has listed its files.
    /// </summary>
    private async Task ShowFilesAsync(string query)
    {
        var version = ++_version;
        var index = _files();
        var current = index.Current;
        if (current.Count == 0 && !index.IsFresh)
        {
            Show([], "Listing files…", query);
        }
        else
        {
            await ShowMatchesAsync(version, current, query).ConfigureAwait(false);
        }
        if (!index.IsFresh)
        {
            var fresh = await index.GetAsync().ConfigureAwait(false);
            await ShowMatchesAsync(version, fresh, query).ConfigureAwait(false);
        }
    }

    private async Task ShowMatchesAsync(int version, IReadOnlyList<IndexedPath> paths, string query)
    {
        var matches = await Task.Run(() => PathMatcher.Match(paths, query, MaxItems)).ConfigureAwait(false);
        var items = matches.Select(p => new CompletionItem
        {
            Value = p.Path,
            Title = p.IsFolder ? p.Name + "/" : p.Name,
            Detail = p.Parent.Length > 0 ? p.Parent : null,
            IsFolder = p.IsFolder,
            IconKey = p.IsFolder ? "IconToolFolder" : "IconToolRead",
        }).ToArray();
        _dispatcher.Post(() =>
        {
            if (version == _version)
            {
                Show(items, paths.Count == 0 ? "No files in this folder" : "No matching files", query);
            }
        });
    }

    private void Show(IReadOnlyList<CompletionItem> items, string emptyMessage, string query)
    {
        // A refresh of the same query keeps the highlight where it was; new typing starts again at the best match.
        var selected = query == _shownQuery ? SelectedItem?.Value : null;
        _shownQuery = query;
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        Message = items.Count == 0 ? emptyMessage : null;
        var keep = selected is null ? -1 : items.ToList().FindIndex(i => i.Value == selected);
        SelectedIndex = -1;
        SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, keep);
        IsOpen = true;
    }
}
