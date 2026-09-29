using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.Conversation;

public sealed partial class TodoItem(string content, string? activeForm, string status) : ObservableObject
{
    public string? Id { get; set; }

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
                Items.Clear();
                foreach (var todo in (input["todos"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    Items.Add(new TodoItem(Text(todo, "content") ?? "", Text(todo, "activeForm"), Text(todo, "status") ?? "pending"));
                }
                break;
            case "TaskCreate":
                var item = new TodoItem(Text(input, "subject") ?? "", Text(input, "activeForm"), "pending");
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
                            existing.Status = status;
                        }
                    }
                    existing.Content = Text(input, "subject") ?? existing.Content;
                    existing.ActiveForm = Text(input, "activeForm") ?? existing.ActiveForm;
                }
                break;
            default:
                return false;
        }
        Changed();
        return true;
    }

    /// <summary>A <c>TaskCreate</c> result carries the new task's id, which later <c>TaskUpdate</c> calls use.</summary>
    public void ApplyToolResult(string toolUseId, string resultText, JsonNode? toolUseResult)
    {
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

    public void Clear()
    {
        Items.Clear();
        _pendingCreates.Clear();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Summary));
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

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex TaskNumber();
}
