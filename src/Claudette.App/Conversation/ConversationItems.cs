using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;

namespace Claudette.App.Conversation;

/// <summary>One row in a tab's conversation (DESIGN.md §5).</summary>
public abstract class ConversationItem : ObservableObject;

public sealed class UserMessageItem(string text) : ConversationItem
{
    public string Text { get; } = text;
}

/// <summary>Assistant text, streamed in as Markdown.</summary>
public sealed partial class AssistantTextItem : ConversationItem
{
    public ObservableStringBuilder Markdown { get; } = new();

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    public string Text => Markdown.ToString();

    public void Append(string text) => Markdown.Append(text);
}

/// <summary>A tool call. Milestone 1 shows a one-line summary; milestone 2 adds full cards.</summary>
public sealed partial class ToolUseItem(string toolUseId, string name, string summary) : ConversationItem
{
    public string ToolUseId { get; } = toolUseId;

    public string Name { get; } = name;

    public string Summary { get; } = summary;

    [ObservableProperty]
    public partial string? ResultSummary { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial bool IsComplete { get; set; }

    /// <summary>The most telling input field, such as the file path or command.</summary>
    public static string Summarize(JsonObject input)
    {
        foreach (var key in new[] { "file_path", "command", "pattern", "path", "url", "query", "description", "prompt" })
        {
            if (input[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                var text = value.GetValue<string>().ReplaceLineEndings(" ");
                return text.Length > 120 ? text[..117] + "…" : text;
            }
        }
        return "";
    }
}

public enum PermissionState
{
    Pending,
    Allowed,
    Denied,
    Cancelled,
}

/// <summary>An inline permission prompt. Milestone 1 offers Allow and Deny; milestone 4 adds Always allow.</summary>
public sealed partial class PermissionItem(PermissionRequest request) : ConversationItem
{
    public PermissionRequest Request { get; } = request;

    public string Title { get; } = $"Allow {request.DisplayName ?? request.ToolName}?";

    public string Detail { get; } = ToolUseItem.Summarize(request.Input) is { Length: > 0 } summary ? summary : request.Description ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(Outcome))]
    public partial PermissionState State { get; set; } = PermissionState.Pending;

    public bool IsPending => State == PermissionState.Pending;

    public string Outcome => State switch
    {
        PermissionState.Allowed => "Allowed",
        PermissionState.Denied => "Denied",
        PermissionState.Cancelled => "No longer needed",
        _ => "",
    };

    [RelayCommand]
    private void Allow()
    {
        if (IsPending)
        {
            Request.Allow();
            State = PermissionState.Allowed;
        }
    }

    [RelayCommand]
    private void Deny()
    {
        if (IsPending)
        {
            Request.Deny("The user denied this action.");
            State = PermissionState.Denied;
        }
    }
}

public enum NoteKind
{
    Info,
    Warning,
    Error,
}

/// <summary>A system note: a model change, an error, the process exiting.</summary>
public sealed class NoteItem(string text, NoteKind kind) : ConversationItem
{
    public string Text { get; } = text;

    public NoteKind Kind { get; } = kind;

    public bool IsError => Kind == NoteKind.Error;

    public bool IsWarning => Kind == NoteKind.Warning;
}
