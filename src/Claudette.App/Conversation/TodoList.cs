using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.Conversation;

public sealed partial class TodoItem(string content, string? activeForm, string status) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    public partial string? Id { get; set; }

    /// <summary>"#3", the task's number as Claude refers to it; empty for a TodoWrite item, which has none.</summary>
    public string NumberText => Id is { Length: > 0 } id ? $"#{id}" : "";

    /// <summary>What needs to be done, in more words than <see cref="Content"/> (TaskCreate's <c>description</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string? Description { get; set; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>Who's working on it, such as a subagent or teammate, when Claude says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwner))]
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
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial DateTimeOffset? CreatedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial DateTimeOffset? StartedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial DateTimeOffset? CompletedAt { get; set; }

    /// <summary>"Took 4m", "Started 14:05" or "Added 14:02", for the Tasks page.</summary>
    public string? TimeText =>
        CompletedAt is { } done && StartedAt is { } began ? $"Took {Duration(done - began)}"
        : CompletedAt is { } finished ? $"Done {finished.ToLocalTime():t}"
        : StartedAt is { } started ? $"Started {started.ToLocalTime():t}"
        : CreatedAt is { } created ? $"Added {created.ToLocalTime():t}"
        : null;

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{Math.Max(0, (int)span.TotalSeconds)}s",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes}m",
        _ => $"{(int)span.TotalHours}h {span.Minutes:00}m",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
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

/// <summary>
/// The to-do list pinned at the top of the conversation (DESIGN.md §5). Fed by <c>TodoWrite</c>, or by the
/// <c>TaskCreate</c>/<c>TaskUpdate</c> tools when a session uses those instead.
/// </summary>
public sealed partial class TodoList : ObservableObject
{
    private readonly Dictionary<string, TodoItem> _pendingCreates = [];

    public ObservableCollection<TodoItem> Items { get; } = [];

    /// <summary>The clock that dates each task's changes, for the Tasks page (DESIGN.md §5, "Tasks"). Null: no times.</summary>
    public TimeProvider? Time { get; set; }

    /// <summary>The plan the user approved last (ExitPlanMode), shown above the tasks; null before one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan), nameof(HasAnything), nameof(PlanMarkdown))]
    public partial string? Plan { get; private set; }

    /// <summary>The plan, for the Markdown view.</summary>
    public LiveMarkdown.Avalonia.ObservableStringBuilder PlanMarkdown => new(Plan ?? "");

    /// <summary>"Approved 14:05", under the plan.</summary>
    public string? PlanApprovedText => PlanApprovedAt is { } at ? $"Approved {at.ToLocalTime():t}" : null;

    public bool HasPlan => !string.IsNullOrWhiteSpace(Plan);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanApprovedText))]
    public partial DateTimeOffset? PlanApprovedAt { get; private set; }

    /// <summary>The Tasks page has something to show.</summary>
    public bool HasAnything => HasItems || HasPlan;

    /// <summary>"2 of 5", for the side panel's Tasks button; empty without tasks.</summary>
    public string Badge => Items.Count == 0 ? "" : $"{Items.Count(i => i.IsDone)} of {Items.Count}";

    /// <summary>The one Claude is working on now, if any.</summary>
    public TodoItem? Current => Items.FirstOrDefault(i => i.IsActive);

    /// <summary>A plan the user approved: the Tasks page shows it above the tasks it turns into.</summary>
    public void SetPlan(string plan)
    {
        Plan = plan.Trim();
        PlanApprovedAt = Now();
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public bool HasItems => Items.Count > 0;

    public string Summary => $"To-do · {Items.Count(i => i.IsDone)} of {Items.Count} done";

    public static bool IsTodoTool(string name) => name is "TodoWrite" or "TaskCreate" or "TaskUpdate";

    /// <summary>Applies a to-do tool call. Returns true if it was one.</summary>
    public bool ApplyToolUse(string toolUseId, string name, JsonObject input)
    {
        switch (name)
        {
            case "TodoWrite":
                // The whole list again: items that read the same keep their times.
                var previous = Items.ToList();
                Items.Clear();
                foreach (var todo in (input["todos"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var content = Text(todo, "content") ?? "";
                    var status = Text(todo, "status") ?? "pending";
                    var same = previous.FirstOrDefault(p => p.Content == content);
                    var todoItem = new TodoItem(content, Text(todo, "activeForm"), same?.Status ?? "pending")
                    {
                        CreatedAt = same?.CreatedAt ?? Now(),
                        StartedAt = same?.StartedAt,
                        CompletedAt = same?.CompletedAt,
                    };
                    SetStatus(todoItem, status);
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
                            SetStatus(existing, status);
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
    public void ApplyToolResult(string toolUseId, string resultText, JsonNode? toolUseResult)
    {
        if (toolUseResult?["tasks"] is JsonArray listed)
        {
            ApplyTaskList(listed);
            return;
        }
        if (!_pendingCreates.Remove(toolUseId, out var item))
        {
            return;
        }
        item.Id = (toolUseResult?["task"]?["id"] ?? toolUseResult?["taskId"] ?? toolUseResult?["id"]) switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
            _ => TaskNumber().Match(resultText) is { Success: true } m ? m.Groups[1].Value : null,
        };
    }

    private void ApplyTaskList(JsonArray listed)
    {
        foreach (var task in listed.OfType<JsonObject>())
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
        Plan = null;
        PlanApprovedAt = null;
        Changed();
    }

    private void SetStatus(TodoItem item, string status)
    {
        if (status == item.Status && item.CreatedAt is not null)
        {
            return;
        }
        var now = Now();
        if (status == "in_progress")
        {
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

    private static string? Text(JsonObject obj, string name) => obj[name] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
        _ => null,
    };

    private static IEnumerable<string> Ids(JsonObject obj, string name) =>
        (obj[name] as JsonArray ?? []).Select(v => v switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        }).OfType<string>();

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex TaskNumber();
}
