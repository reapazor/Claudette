using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.Conversation;

/// <summary>
/// Find in the conversation (DESIGN.md §5, "Find"): the items whose text has the query, ignoring case, in order, with
/// one of them current. Looks through prompts, replies, thinking, tool calls with their output, notes, hook runs and
/// subagents' items. The view scrolls to <see cref="Current"/> and marks it.
/// </summary>
public sealed partial class ConversationSearch : ObservableObject
{
    private readonly ObservableCollection<ConversationItem> _items;
    private List<ConversationItem> _matches = [];

    public ConversationSearch(ObservableCollection<ConversationItem> items)
    {
        _items = items;
        // New items while the bar is open count too, without moving the current one.
        _items.CollectionChanged += OnItemsChanged;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
        {
            Query = "";
        }
    }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    partial void OnQueryChanged(string value) => Search(keepCurrent: false);

    /// <summary>The match the view shows: the newest one after a new search, as the conversation is read bottom up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial ConversationItem? Current { get; private set; }

    /// <summary>Raised when <see cref="Current"/> changes to a match, so the view brings it into view.</summary>
    public event Action<ConversationItem>? CurrentChanged;

    public int Count => _matches.Count;

    /// <summary>"3 of 12", "No matches", or empty before anything is typed.</summary>
    public string CountText => Query.Trim().Length == 0 ? ""
        : _matches.Count == 0 ? "No matches"
        : $"{_matches.IndexOf(Current!) + 1} of {_matches.Count}";

    [RelayCommand]
    private void Open() => IsOpen = true;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Next() => Step(1);

    [RelayCommand]
    private void Previous() => Step(-1);

    private void Step(int by)
    {
        if (_matches.Count == 0)
        {
            return;
        }
        var at = Current is null ? -1 : _matches.IndexOf(Current);
        SetCurrent(_matches[(at + by + _matches.Count) % _matches.Count]);
    }

    private void SetCurrent(ConversationItem? item)
    {
        Current = item;
        OnPropertyChanged(nameof(CountText));
        if (item is not null)
        {
            CurrentChanged?.Invoke(item);
        }
    }

    /// <summary>
    /// How many items before newly added ones are looked through again: they may still have been filling in, such as a
    /// reply streaming in before the next item came.
    /// </summary>
    private const int StillFillingIn = 3;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsOpen || Query.Trim().Length == 0)
        {
            return;
        }
        if (e is { Action: NotifyCollectionChangedAction.Add, NewItems: { } added } && e.NewStartingIndex == _items.Count - added.Count)
        {
            // Only the new items, and the few before them, rather than the whole conversation again.
            var from = Math.Max(0, e.NewStartingIndex - StillFillingIn);
            var query = Query.Trim();
            var again = _items.Skip(from).ToList();
            _matches.RemoveAll(again.Contains);
            _matches.AddRange(again.Where(item => Matches(item, query)));
            OnPropertyChanged(nameof(Count));
            if (Current is null || !_matches.Contains(Current))
            {
                SetCurrent(_matches.LastOrDefault());
            }
            else
            {
                OnPropertyChanged(nameof(CountText));
            }
            return;
        }
        Search(keepCurrent: true);
    }

    private void Search(bool keepCurrent)
    {
        var query = Query.Trim();
        var previous = Current;
        _matches = query.Length == 0 ? [] : [.. _items.Where(item => Matches(item, query))];
        OnPropertyChanged(nameof(Count));
        if (keepCurrent && previous is not null && _matches.Contains(previous))
        {
            OnPropertyChanged(nameof(CountText));
            return;
        }
        SetCurrent(_matches.LastOrDefault());
    }

    /// <summary>
    /// The match is only in what a collapsed card hides (a tool's input and output, a thought, a subagent's work), so
    /// the card opens to show it.
    /// </summary>
    public bool IsHidden(ConversationItem item)
    {
        var query = Query.Trim();
        return item switch
        {
            ThinkingItem thinking => !thinking.IsExpanded,
            ToolUseItem tool => !tool.IsExpanded && !Has(tool.Name, query) && !Has(tool.Summary, query),
            HookRunItem hook => !hook.IsExpanded && !Has(hook.Title, query),
            UserMessageItem user => user.IsTextCut && !Has(user.ShownText, query) && !Has(user.SuffixText, query),
            _ => false,
        };
    }

    private static bool Matches(ConversationItem item, string query) => item switch
    {
        SubagentItem agent => Has(agent.Name, query) || Has(agent.FullSummary, query) || Has(agent.Output, query) || agent.Items.Any(child => Matches(child, query)),
        _ => TextOf(item).Any(text => Has(text, query)),
    };

    /// <summary>What Find looks through in an item.</summary>
    internal static IEnumerable<string?> TextOf(ConversationItem item) => item switch
    {
        UserMessageItem user => [user.CopyText],
        AssistantTextItem reply => [reply.Text],
        ThinkingItem thinking => [thinking.Text],
        ToolUseItem tool => [tool.Name, tool.FullSummary, tool.Detail, tool.Output, tool.ResultSummary],
        NoteItem note => [note.Text],
        HookRunItem hook => [hook.Title, hook.Output],
        McpInputItem input => [input.Title, input.Message],
        PromptItem prompt => [prompt.Outcome, prompt.Request.ToolName, prompt is PlanItem plan ? plan.Plan.ToString() : null],
        TurnSummaryItem summary => [summary.Text],
        _ => [],
    };

    private static bool Has(string? text, string query) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
