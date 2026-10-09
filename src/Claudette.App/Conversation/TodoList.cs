using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claudette.Core;
using Claudette.Core.Diffs;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.Conversation;

/// <summary>A file Claude changed while a task was in progress (DESIGN.md §5, "Tasks").</summary>
public sealed class TaskFileRow(string path, string toolUseId)
{
    public string Path { get; } = path;

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The first change to it under the task: Changed files finds the file by it.</summary>
    public string ToolUseId { get; } = toolUseId;
}

public sealed partial class TodoItem(string content, string? activeForm, string status) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText), nameof(Title))]
    public partial string? Id { get; set; }

    /// <summary>"#3", the task's number as Claude refers to it; empty for a TodoWrite item, which has none.</summary>
    public string NumberText => Id is { Length: > 0 } id ? $"#{id}" : "";

    /// <summary>"#3 Run the migration": its number, when it has one, and its subject.</summary>
    public string Title => NumberText.Length > 0 ? $"{NumberText} {Content}" : Content;

    /// <summary>What needs to be done, in more words than <see cref="Content"/> (TaskCreate's <c>description</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string? Description { get; set; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>Who's working on it, such as a subagent or teammate, when Claude says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwner), nameof(DetailText), nameof(HasDetail), nameof(HasDetailLine), nameof(FilesLinkText), nameof(TokensSuffix))]
    public partial string? Owner { get; set; }

    public bool HasOwner => !string.IsNullOrWhiteSpace(Owner);

    /// <summary>The tasks that have to finish first, by id.</summary>
    public ObservableCollection<string> BlockedBy { get; } = [];

    /// <summary>"Waiting on #1, #2" while any of them isn't done; set by the list, which knows their state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    public partial string? BlockedText { get; set; }

    public bool IsBlocked => BlockedText is not null;

    /// <summary>When Claude added it, started it and finished it, by the tab's clock; null when not seen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(DetailText), nameof(HasDetail), nameof(HasDetailLine), nameof(FilesLinkText), nameof(TokensSuffix))]
    public partial DateTimeOffset? CreatedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(DetailText), nameof(HasDetail), nameof(HasDetailLine), nameof(FilesLinkText), nameof(TokensSuffix))]
    public partial DateTimeOffset? StartedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(DetailText), nameof(HasDetail), nameof(HasDetailLine), nameof(FilesLinkText), nameof(TokensSuffix))]
    public partial DateTimeOffset? CompletedAt { get; set; }

    /// <summary>"Took 4m", "Started 14:05" or "Added 14:02", for the Tasks page.</summary>
    public string? TimeText =>
        CompletedAt is { } done && StartedAt is { } began ? $"Took {Formats.Duration(done - began)}"
        : CompletedAt is { } finished ? $"Done {finished.ToLocalTime():t}"
        : StartedAt is { } started ? $"Started {started.ToLocalTime():t}"
        : CreatedAt is { } created ? $"Added {created.ToLocalTime():t}"
        : null;

    /// <summary>"Explore · Took 4m": who's on it and its time, under the task on the Tasks page; null with neither.</summary>
    public string? DetailText => (HasOwner, TimeText) switch
    {
        (true, { } time) => $"{Owner} · {time}",
        (true, null) => Owner,
        (false, var time) => time,
    };

    public bool HasDetail => DetailText is not null;

    /// <summary>The line under the task has something: who and when, its files or its tokens.</summary>
    public bool HasDetailLine => HasDetail || HasFiles || HasTokens;

    /// <summary>The row in the conversation where it started (DESIGN.md §5, "Tasks"); null until it has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStart))]
    public partial TaskStartItem? Start { get; internal set; }

    public bool HasStart => Start is not null;

    /// <summary>
    /// Who put it in progress: the conversation builder of the agent whose call did, so its work and its subagents' count
    /// toward it. Null when no call did (a <c>TaskList</c> result said so): the main agent's.
    /// </summary>
    internal object? StartedBy { get; set; }

    /// <summary>The files Claude changed while it was in progress, in the order first changed.</summary>
    public ObservableCollection<TaskFileRow> Files { get; } = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>"· 3 files", after who and when.</summary>
    public string FilesLinkText => (HasDetail ? "· " : "") + (Files.Count == 1 ? "1 file" : $"{Files.Count} files");

    internal void AddFile(string path, string toolUseId)
    {
        if (Files.Any(f => ChangedFiles.PlatformPathComparer.Equals(f.Path, path)))
        {
            return;
        }
        Files.Add(new TaskFileRow(path, toolUseId));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FilesLinkText));
        OnPropertyChanged(nameof(HasDetailLine));
        OnPropertyChanged(nameof(TokensSuffix));
    }

    /// <summary>Each call's tokens, by message id, as the working line counts them.</summary>
    private readonly Dictionary<string, long> _calls = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens), nameof(TokensSuffix), nameof(HasDetailLine))]
    public partial long Tokens { get; private set; }

    public bool HasTokens => Tokens > 0;

    /// <summary>"· 120k tokens", after who, when and the files.</summary>
    public string TokensSuffix => (HasDetail || HasFiles ? "· " : "") + $"{TokenTotals.Short(Tokens)} tokens";

    internal void SetCallTokens(string callId, long tokens)
    {
        _calls[callId] = tokens;
        Tokens = _calls.Values.Sum();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText), nameof(Title))]
    public partial string Content { get; set; } = content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial string? ActiveForm { get; set; } = activeForm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsActive), nameof(Glyph), nameof(DisplayText))]
    public partial string Status { get; set; } = status;

    public bool IsDone => Status == "completed";

    public bool IsActive => Status == "in_progress";

    public string Glyph => Status switch
    {
        "completed" => "☑",
        "in_progress" => "◐",
        _ => "☐",
    };

    /// <summary>The in-progress item shows its "active form", for example "Running the tests".</summary>
    public string DisplayText => IsActive && !string.IsNullOrEmpty(ActiveForm) ? ActiveForm : Content;
}

/// <summary>Where the Tasks page's plan came from (DESIGN.md §5, "Tasks").</summary>
public enum PlanSource
{
    /// <summary>A plan Claude wrote in Plan mode (ExitPlanMode).</summary>
    Approved,

    /// <summary>A reply the user chose with <b>Show as the plan</b>.</summary>
    Reply,
}

/// <summary>What became of one version of the plan (DESIGN.md §5, "Tasks").</summary>
public enum PlanState
{
    /// <summary>Claude is writing it in Plan mode.</summary>
    Drafting,

    /// <summary>Put up for review (ExitPlanMode), not answered yet.</summary>
    Waiting,

    Approved,

    /// <summary>The user kept planning, with or without feedback.</summary>
    SentBack,

    /// <summary>The turn ended, or Claude Code stopped, before it was answered.</summary>
    Unanswered,

    /// <summary>A reply the user chose with <b>Show as the plan</b>.</summary>
    Reply,
}

/// <summary>One version of the plan: a draft, a plan put up for review, or a reply chosen as the plan.</summary>
public sealed partial class PlanVersion : ObservableObject
{
    internal PlanVersion(int number, PlanState state, string text, DateTimeOffset? at, PlanVersion? previous)
    {
        Number = number;
        State = state;
        Text = text;
        At = at;
        Previous = previous;
    }

    /// <summary>Its number in the session, from 1.</summary>
    public int Number { get; }

    /// <summary>The version before it, which <see cref="Changes"/> compares it with.</summary>
    public PlanVersion? Previous { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Markdown), nameof(Changes))]
    public partial string Text { get; internal set; }

    /// <summary>The plan, for the Markdown view.</summary>
    public LiveMarkdown.Avalonia.ObservableStringBuilder Markdown => new(Text);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial PlanState State { get; internal set; }

    /// <summary>
    /// When it started being written, was put up, approved or sent back, or when Claude wrote the reply it came from;
    /// null when not known.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial DateTimeOffset? At { get; internal set; }

    /// <summary>What the user said when they sent it back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? Feedback { get; internal set; }

    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);

    /// <summary>The ExitPlanMode call that put it up for review.</summary>
    internal string? ToolUseId { get; set; }

    /// <summary>"Approved 14:05", "Drafting since 14:02", "From Claude's reply at 14:03" and so on.</summary>
    public string TimeText => (State, At) switch
    {
        (PlanState.Drafting, { } at) => $"Drafting since {at.ToLocalTime():t}",
        (PlanState.Drafting, null) => "Drafting",
        (PlanState.Waiting, _) => "Waiting for review",
        (PlanState.Approved, { } at) => $"Approved {at.ToLocalTime():t}",
        (PlanState.Approved, null) => "Approved",
        (PlanState.SentBack, { } at) => $"Sent back {at.ToLocalTime():t}",
        (PlanState.SentBack, null) => "Sent back",
        (PlanState.Unanswered, _) => "Not answered",
        (PlanState.Reply, { } at) => $"From Claude's reply at {at.ToLocalTime():t}",
        _ => "From Claude's reply",
    };

    public bool HasPrevious => Previous is not null;

    /// <summary>"Changes from v2".</summary>
    public string ChangesLabel => Previous is { } previous ? $"Changes from v{previous.Number}" : "";

    /// <summary>This version as a line diff against the one before it.</summary>
    public DiffView Changes => DiffView.FromTexts(Previous?.Text, Text);
}

/// <summary>
/// The to-do list pinned at the top of the conversation and the Tasks page (DESIGN.md §5, "Tasks"). Fed by <c>TodoWrite</c>,
/// or by the <c>TaskCreate</c>/<c>TaskUpdate</c> tools when a session uses those instead, and the plan's versions.
/// </summary>
public sealed partial class TodoList : ObservableObject
{
    private readonly Dictionary<string, TodoItem> _pendingCreates = [];

    /// <summary><c>TaskList</c> and <c>TaskGet</c> calls whose results haven't come.</summary>
    private readonly HashSet<string> _pendingReads = [];

    /// <summary>The tasks the latest tool call put in progress.</summary>
    private readonly List<TodoItem> _started = [];

    /// <summary>
    /// The task each call's tokens count toward, by message id; null for none. A call is repeated on each of its messages,
    /// so the task in progress at its first one has it.
    /// </summary>
    private readonly Dictionary<string, TodoItem?> _callTasks = new(StringComparer.Ordinal);

    public ObservableCollection<TodoItem> Items { get; } = [];

    /// <summary>The clock that dates each task's changes, for the Tasks page (DESIGN.md §5, "Tasks"). Null: no times.</summary>
    public TimeProvider? Time { get; set; }

    // ---- The plan (DESIGN.md §5, "Tasks") ------------------------------------------------------------------------

    /// <summary>Every version of the plan in the session, oldest first.</summary>
    public ObservableCollection<PlanVersion> Plans { get; } = [];

    /// <summary>The newest version: the one the page follows.</summary>
    public PlanVersion? NewestPlan => Plans.Count > 0 ? Plans[^1] : null;

    /// <summary>The newest version's text; null before there's one.</summary>
    public string? Plan => NewestPlan?.Text;

    /// <summary>Where the newest version came from.</summary>
    public PlanSource PlanSource => NewestPlan?.State == PlanState.Reply ? PlanSource.Reply : PlanSource.Approved;

    /// <summary>When the newest version was approved, or written; null when not known.</summary>
    public DateTimeOffset? PlanAt => NewestPlan?.At;

    public bool HasPlan => !string.IsNullOrWhiteSpace(Plan);

    /// <summary>The version the page shows: the newest, unless the user stepped back to an earlier one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownPlanPosition), nameof(ShowsPlanChanges))]
    [NotifyCanExecuteChangedFor(nameof(ShowEarlierPlanCommand), nameof(ShowLaterPlanCommand))]
    public partial PlanVersion? ShownPlan { get; private set; }

    /// <summary>The page shows the plan as its changes: asked for, and it has a version before it.</summary>
    public bool ShowsPlanChanges => ShowPlanChanges && ShownPlan?.HasPrevious == true;

    /// <summary>"v2 of 3", with more than one version.</summary>
    public string ShownPlanPosition => ShownPlan is { } shown && Plans.Count > 1 ? $"v{shown.Number} of {Plans.Count}" : "";

    public bool HasManyPlans => Plans.Count > 1;

    /// <summary>The page shows the plan as a line diff against the version before it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlanChanges))]
    public partial bool ShowPlanChanges { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanShowEarlierPlan))]
    private void ShowEarlierPlan() => ShownPlan = ShownPlan?.Previous ?? ShownPlan;

    private bool CanShowEarlierPlan() => ShownPlan?.Previous is not null;

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanShowLaterPlan))]
    private void ShowLaterPlan()
    {
        if (ShownPlan is { } shown && Plans.IndexOf(shown) is var index and >= 0 && index < Plans.Count - 1)
        {
            ShownPlan = Plans[index + 1];
        }
    }

    private bool CanShowLaterPlan() => ShownPlan is { } shown && !ReferenceEquals(shown, NewestPlan);

    /// <summary>Claude is writing a plan in Plan mode.</summary>
    public bool IsDrafting => NewestPlan?.State == PlanState.Drafting;

    /// <summary>The draft so far, which an Edit to the plan file changes; null while there's none.</summary>
    internal string? DraftText => NewestPlan is { State: PlanState.Drafting } draft ? draft.Text : null;

    /// <summary>The plan's draft changed: Claude wrote or edited its plan file in Plan mode.</summary>
    public void OnPlanDrafted(string text)
    {
        if (NewestPlan is { State: PlanState.Drafting } draft)
        {
            draft.Text = text.Trim();
        }
        else
        {
            AddPlan(PlanState.Drafting, text.Trim(), Now());
        }
        PlanChanged();
    }

    /// <summary>
    /// Claude put a plan up for review (ExitPlanMode): the draft, with the request's text when it has one, else a new
    /// version. Null when there's no text for one.
    /// </summary>
    public PlanVersion? OnPlanProposed(string? toolUseId, string? plan)
    {
        PlanVersion version;
        if (NewestPlan is { State: PlanState.Drafting } draft)
        {
            version = draft;
            if (!string.IsNullOrWhiteSpace(plan))
            {
                draft.Text = plan.Trim();
            }
            draft.State = PlanState.Waiting;
            draft.At = Now();
        }
        else if (!string.IsNullOrWhiteSpace(plan))
        {
            version = AddPlan(PlanState.Waiting, plan.Trim(), Now());
        }
        else
        {
            return null;
        }
        version.ToolUseId = toolUseId;
        PlanChanged();
        return version;
    }

    /// <summary>
    /// What became of a plan Claude put up for review, from its result: approved, sent back with
    /// <paramref name="feedback"/>, or <paramref name="interrupted"/> before anyone answered. A plan seen only now (in a
    /// transcript) becomes a version when there's text for it.
    /// </summary>
    public void OnPlanAnswered(string toolUseId, bool approved, string? plan, string? feedback = null, bool interrupted = false)
    {
        var version = Plans.LastOrDefault(p => p.ToolUseId == toolUseId);
        if (version is null)
        {
            if (string.IsNullOrWhiteSpace(plan))
            {
                return;
            }
            version = NewestPlan is { State: PlanState.Drafting } draft ? draft : AddPlan(PlanState.Waiting, plan.Trim(), Now());
            version.ToolUseId = toolUseId;
        }
        if (!string.IsNullOrWhiteSpace(plan))
        {
            version.Text = plan.Trim();
        }
        version.State = approved ? PlanState.Approved : interrupted ? PlanState.Unanswered : PlanState.SentBack;
        version.At = interrupted ? version.At : Now();
        version.Feedback = approved || interrupted ? null : feedback;
        PlanChanged();
    }

    /// <summary>The turn ended, or Claude Code stopped: a plan still waiting for review was never answered.</summary>
    public void EndWaitingPlans()
    {
        var waiting = Plans.Where(p => p.State == PlanState.Waiting).ToArray();
        foreach (var version in waiting)
        {
            version.State = PlanState.Unanswered;
        }
        if (waiting.Length > 0)
        {
            PlanChanged();
        }
    }

    /// <summary>
    /// <b>Show as the plan</b>: a reply Claude wrote at <paramref name="writtenAt"/>. It replaces a chosen reply that's
    /// the newest version, and the page shows it.
    /// </summary>
    public void ChooseReply(string text, DateTimeOffset? writtenAt)
    {
        if (NewestPlan is { State: PlanState.Reply } chosen)
        {
            chosen.Text = text.Trim();
            chosen.At = writtenAt;
        }
        else
        {
            AddPlan(PlanState.Reply, text.Trim(), writtenAt);
        }
        ShownPlan = NewestPlan;
        PlanChanged();
    }

    private PlanVersion AddPlan(PlanState state, string text, DateTimeOffset? at)
    {
        var following = ShownPlan is null || ReferenceEquals(ShownPlan, NewestPlan);
        var version = new PlanVersion(Plans.Count + 1, state, text, at, NewestPlan);
        Plans.Add(version);
        if (following)
        {
            ShownPlan = version;
        }
        // A plan came: no need to offer a reply as one.
        SuggestedReply = null;
        return version;
    }

    private void PlanChanged()
    {
        OnPropertyChanged(nameof(NewestPlan));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(PlanSource));
        OnPropertyChanged(nameof(PlanAt));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasAnything));
        OnPropertyChanged(nameof(HasManyPlans));
        OnPropertyChanged(nameof(ShownPlanPosition));
        OnPropertyChanged(nameof(IsDrafting));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        ShowEarlierPlanCommand.NotifyCanExecuteChanged();
        ShowLaterPlanCommand.NotifyCanExecuteChanged();
    }

    // ---- Suggesting a reply as the plan ----------------------------------------------------------------------------

    /// <summary>A reply that looks like the plan for the task list Claude just started; null while none is offered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestion), nameof(SuggestionText))]
    public partial AssistantTextItem? SuggestedReply { get; private set; }

    public bool HasSuggestion => SuggestedReply is not null;

    /// <summary>"Use Claude's reply from 14:03 as the plan?"</summary>
    public string SuggestionText => SuggestedReply?.SentAt is { } at
        ? $"Use Claude's reply from {at.ToLocalTime():t} as the plan?"
        : "Use Claude's latest reply as the plan?";

    internal void Suggest(AssistantTextItem reply) => SuggestedReply = reply;

    public void DismissSuggestion() => SuggestedReply = null;

    /// <summary>A <c>TodoWrite</c> or <c>TaskCreate</c> now would start a new list: there's none, or it's all done.</summary>
    internal bool StartsNewList => Items.All(i => i.IsDone);

    /// <summary>A reply with a list of three or more items, numbered or bulleted, reads as a plan.</summary>
    internal static bool LooksLikePlan(string text) => ListItem().Count(text) >= 3;

    [GeneratedRegex(@"^[ \t]*(?:\d+[.)]|[-*+])[ \t]+\S", RegexOptions.Multiline)]
    private static partial Regex ListItem();

    // ---- Progress -------------------------------------------------------------------------------------------------

    /// <summary>The Tasks page has something to show.</summary>
    public bool HasAnything => HasItems || HasPlan;

    /// <summary>"2 of 5", for the side panel's Tasks button; empty without tasks.</summary>
    public string Badge => Items.Count == 0 ? "" : $"{DoneCount} of {Items.Count}";

    public int DoneCount => Items.Count(i => i.IsDone);

    /// <summary>There are tasks, and they're all done.</summary>
    public bool AllDone => Items.Count > 0 && Items.All(i => i.IsDone);

    /// <summary>Some tasks aren't done.</summary>
    public bool HasTasksLeft => Items.Any(i => !i.IsDone);

    /// <summary>"3/7", for the tab's row.</summary>
    public string ProgressShort => $"{DoneCount}/{Items.Count}";

    /// <summary>"3 of 7 tasks done · Now: Running the tests", the row badge's tip.</summary>
    public string ProgressTip => $"{DoneCount} of {Items.Count} tasks done{NowText}";

    /// <summary>"3 of 7 done · Now: Running the tests", for the tab info card; null without tasks.</summary>
    public string? InfoText => Items.Count == 0 ? null : $"{DoneCount} of {Items.Count} done{NowText}";

    private string NowText => Current is { } current ? $" · Now: {current.DisplayText}" : "";

    /// <summary>
    /// The status line's words (DESIGN.md §5, "Tasks"): "Drafting the plan…", "3 of 7 tasks · Running the tests" or "All 7
    /// tasks done"; null with nothing to say.
    /// </summary>
    public string? StatusText =>
        IsDrafting ? "Drafting the plan…"
        : HasTasksLeft ? $"{DoneCount} of {Count(Items.Count)}" + (Current is { } current ? $" · {current.DisplayText}" : "")
        : AllDone ? AllDoneText
        : null;

    public bool HasStatus => StatusText is not null;

    private string AllDoneText => Items.Count == 1 ? "Task done" : $"All {Items.Count} tasks done";

    private static string Count(int tasks) => tasks == 1 ? "1 task" : $"{tasks} tasks";

    /// <summary>
    /// Where the list stands as a turn ends at <paramref name="end"/> (DESIGN.md §5, "Tasks"): "All 7 tasks done · 23m", or
    /// "4 of 7 tasks left · Next: #4 Run the migration". Null without tasks.
    /// </summary>
    internal string? TurnSummaryText(DateTimeOffset? end)
    {
        if (Items.Count == 0)
        {
            return null;
        }
        if (AllDone)
        {
            var first = Items.Select(i => i.StartedAt ?? i.CreatedAt).Where(t => t is not null).Min();
            return first is { } began && end is { } ended && ended > began ? $"{AllDoneText} · {Formats.Duration(ended - began)}" : AllDoneText;
        }
        var left = $"{Items.Count - DoneCount} of {Count(Items.Count)} left";
        if (Current is { } current)
        {
            return $"{left} · In progress: {current.Title}";
        }
        var next = Items.FirstOrDefault(i => !i.IsDone && !i.IsBlocked) ?? Items.First(i => !i.IsDone);
        return $"{left} · Next: {next.Title}";
    }

    /// <summary>The one Claude is working on now, if any.</summary>
    public TodoItem? Current => Items.FirstOrDefault(i => i.IsActive);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public bool HasItems => Items.Count > 0;

    public string Summary => $"To-do · {DoneCount} of {Items.Count} done";

    // ---- What each task did ----------------------------------------------------------------------------------------

    /// <summary>The tasks the latest <see cref="ApplyToolUse"/> put in progress: where they start goes in the conversation.</summary>
    internal IReadOnlyList<TodoItem> Started => _started;

    /// <summary>
    /// The task in progress that work by an agent counts toward (DESIGN.md §5, "What each task did"): one it started, or
    /// else one an agent above it started. <paramref name="agents"/> runs from the agent up to the main agent. An agent
    /// with more than one in progress gives none.
    /// </summary>
    internal TodoItem? TaskFor(IReadOnlyList<object> agents)
    {
        for (var level = 0; level < agents.Count; level++)
        {
            var agent = agents[level];
            var isMain = level == agents.Count - 1;
            var mine = Items.Where(i => i.IsActive && (ReferenceEquals(i.StartedBy, agent) || isMain && i.StartedBy is null)).Take(2).ToArray();
            if (mine.Length > 0)
            {
                return mine.Length == 1 ? mine[0] : null;
            }
        }
        return null;
    }

    /// <summary>A call's tokens, toward <paramref name="task"/> if it's the call's first message, else toward the task its first had.</summary>
    internal void CountCall(string callId, long tokens, TodoItem? task)
    {
        if (!_callTasks.TryGetValue(callId, out var counted))
        {
            _callTasks[callId] = counted = task;
        }
        counted?.SetCallTokens(callId, tokens);
    }

    // ---- The calls ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies a to-do tool call. Returns true if it was one, so it shows here rather than as a card. <c>TaskList</c> and
    /// <c>TaskGet</c> change nothing, but their results say how the tasks stand.
    /// </summary>
    /// <param name="agent">The agent making the call (its conversation builder): a task it starts is its own.</param>
    public bool ApplyToolUse(string toolUseId, string name, JsonObject input, object? agent = null)
    {
        _started.Clear();
        switch (name)
        {
            case "TaskList" or "TaskGet":
                _pendingReads.Add(toolUseId);
                return true;
            case "TodoWrite":
                // The whole list again: items that read the same are the same item, keeping their times and rows.
                var previous = Items.ToList();
                Items.Clear();
                foreach (var todo in (input["todos"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var content = Text(todo, "content") ?? "";
                    var status = Text(todo, "status") ?? "pending";
                    var todoItem = previous.FirstOrDefault(p => p.Content == content);
                    if (todoItem is null)
                    {
                        todoItem = new TodoItem(content, Text(todo, "activeForm"), "pending") { CreatedAt = Now() };
                    }
                    else
                    {
                        previous.Remove(todoItem);
                        todoItem.ActiveForm = Text(todo, "activeForm");
                    }
                    SetStatus(todoItem, status, agent);
                    Items.Add(todoItem);
                }
                break;
            case "TaskCreate":
                var item = new TodoItem(Text(input, "subject") ?? "", Text(input, "activeForm"), "pending")
                {
                    Description = Text(input, "description"),
                    CreatedAt = Now(),
                };
                Items.Add(item);
                _pendingCreates[toolUseId] = item;
                break;
            case "TaskUpdate":
                if (Text(input, "taskId") is { } id && Items.FirstOrDefault(i => i.Id == id) is { } existing)
                {
                    if (Text(input, "status") is { } status)
                    {
                        if (status == "deleted")
                        {
                            Items.Remove(existing);
                        }
                        else
                        {
                            SetStatus(existing, status, agent);
                        }
                    }
                    existing.Content = Text(input, "subject") ?? existing.Content;
                    existing.ActiveForm = Text(input, "activeForm") ?? existing.ActiveForm;
                    existing.Description = Text(input, "description") ?? existing.Description;
                    existing.Owner = Text(input, "owner") ?? existing.Owner;
                    foreach (var blocker in Ids(input, "addBlockedBy"))
                    {
                        if (!existing.BlockedBy.Contains(blocker))
                        {
                            existing.BlockedBy.Add(blocker);
                        }
                    }
                    foreach (var blocked in Ids(input, "addBlocks"))
                    {
                        if (Items.FirstOrDefault(i => i.Id == blocked) is { } other && !other.BlockedBy.Contains(id))
                        {
                            other.BlockedBy.Add(id);
                        }
                    }
                }
                break;
            default:
                return false;
        }
        Changed();
        return true;
    }

    /// <summary>
    /// A <c>TaskCreate</c> result carries the new task's id, which later <c>TaskUpdate</c> calls use. A <c>TaskList</c>
    /// result is the whole list as Claude Code has it: it fills in what the calls didn't show.
    /// </summary>
    /// <param name="toolUseResult">The result's details; an error's is just its text, a string.</param>
    public void ApplyToolResult(string toolUseId, string resultText, JsonNode? toolUseResult)
    {
        var details = toolUseResult as JsonObject;
        if (details?["tasks"] is JsonArray listed)
        {
            ApplyTaskList(listed.OfType<JsonObject>());
            return;
        }
        if (_pendingReads.Remove(toolUseId))
        {
            if (details?["task"] is JsonObject task)
            {
                ApplyTaskList([task]);
            }
            return;
        }
        if (!_pendingCreates.Remove(toolUseId, out var item))
        {
            return;
        }
        item.Id = ((details?["task"] as JsonObject)?["id"] ?? details?["taskId"] ?? details?["id"]).AsStringOrNumber()
            ?? (TaskNumber().Match(resultText) is { Success: true } m ? m.Groups[1].Value : null);
    }

    private void ApplyTaskList(IEnumerable<JsonObject> listed)
    {
        foreach (var task in listed)
        {
            if (Text(task, "id") is not { } id)
            {
                continue;
            }
            var item = Items.FirstOrDefault(i => i.Id == id);
            if (item is null)
            {
                item = new TodoItem(Text(task, "subject") ?? "", null, "pending") { Id = id, CreatedAt = Now() };
                Items.Add(item);
            }
            item.Content = Text(task, "subject") ?? item.Content;
            item.Description = Text(task, "description") ?? item.Description;
            item.Owner = Text(task, "owner") ?? item.Owner;
            if (Text(task, "status") is { } status)
            {
                SetStatus(item, status);
            }
            foreach (var blocker in Ids(task, "blockedBy").Where(b => !item.BlockedBy.Contains(b)))
            {
                item.BlockedBy.Add(blocker);
            }
        }
        Changed();
    }

    public void Clear()
    {
        Items.Clear();
        _pendingCreates.Clear();
        _pendingReads.Clear();
        _started.Clear();
        _callTasks.Clear();
        Plans.Clear();
        ShownPlan = null;
        ShowPlanChanges = false;
        SuggestedReply = null;
        PlanChanged();
        Changed();
    }

    private void SetStatus(TodoItem item, string status, object? agent = null)
    {
        if (status == item.Status && item.CreatedAt is not null)
        {
            return;
        }
        var now = Now();
        if (status == "in_progress")
        {
            if (!item.IsActive)
            {
                item.StartedBy = agent;
                _started.Add(item);
            }
            item.StartedAt ??= now;
            item.CompletedAt = null;
        }
        else if (status == "completed")
        {
            item.CompletedAt ??= now;
        }
        item.Status = status;
    }

    /// <summary>
    /// When a change happens, set by the conversation builder: its clock live, and the transcript entry's time while a
    /// restored tab replays, so a restored task isn't dated by the restore. When it has no time, <see cref="Time"/>.
    /// </summary>
    internal Func<DateTimeOffset?>? Clock { get; set; }

    private DateTimeOffset? Now() => Clock?.Invoke() ?? Time?.GetUtcNow();

    private void Changed()
    {
        // Waiting on another task: shown while any it waits for isn't done.
        foreach (var item in Items)
        {
            var waiting = item.IsDone ? [] : item.BlockedBy.Where(b => Items.FirstOrDefault(i => i.Id == b) is not { IsDone: true }).ToArray();
            item.BlockedText = waiting.Length == 0 ? null : $"Waiting on {string.Join(", ", waiting.Select(b => $"#{b}"))}";
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasAnything));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(AllDone));
        OnPropertyChanged(nameof(HasTasksLeft));
        OnPropertyChanged(nameof(ProgressShort));
        OnPropertyChanged(nameof(ProgressTip));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        // Collapse once everything is done, so a finished list doesn't take up room.
        if (Items.Count > 0 && Items.All(i => i.IsDone))
        {
            IsExpanded = false;
        }
        else if (Items.Any(i => !i.IsDone))
        {
            IsExpanded = true;
        }
    }

    private static string? Text(JsonObject obj, string name) => obj.GetStringOrNumber(name);

    private static IEnumerable<string> Ids(JsonObject obj, string name) =>
        (obj[name] as JsonArray ?? []).Select(v => v.AsStringOrNumber()).OfType<string>();

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex TaskNumber();
}
