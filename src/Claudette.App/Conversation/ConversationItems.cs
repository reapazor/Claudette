using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;

namespace Claudette.App.Conversation;

/// <summary>One row in a tab's conversation (DESIGN.md §5).</summary>
public abstract class ConversationItem : ObservableObject;

public sealed class UserMessageItem(string text, string? suffixText = null, bool isCheckIn = false) : ConversationItem
{
    public string Text { get; } = text;

    /// <summary>Quick suffixes appended to the message, shown in a lighter style (DESIGN.md §5, "Quick suffixes").</summary>
    public string? SuffixText { get; } = suffixText;

    public bool HasSuffix => !string.IsNullOrEmpty(SuffixText);

    /// <summary>Sent by Claudette as an automatic check-in (DESIGN.md §5, "Check-ins on long turns").</summary>
    public bool IsCheckIn { get; } = isCheckIn;

    /// <summary>Attached images, shown as thumbnails (DESIGN.md §5, "Attachments").</summary>
    public IReadOnlyList<MessageImage> Images { get; init; } = [];

    public bool HasImages => Images.Count > 0;
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

/// <summary>Claude's thinking, collapsed by default.</summary>
public sealed partial class ThinkingItem : ConversationItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial bool IsStreaming { get; set; } = true;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool HasText => Text.Length > 0;

    public string Header => IsStreaming ? "Thinking…" : "Thinking";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// A tool call card: icon, name and one-line summary, expandable to the full input and output (DESIGN.md §5).
/// Edit and Write show a diff; Bash shows its command and output.
/// </summary>
public partial class ToolUseItem : ConversationItem
{
    public ToolUseItem(string toolUseId, string name, JsonObject input)
    {
        ToolUseId = toolUseId;
        Name = name;
        Input = input;
        Summary = Summarize(name, input);
        Command = name == "Bash" ? Str(input, "command") : null;
        Detail = DetailText(name, input);
        if (name == "Edit" && Str(input, "old_string") is { } oldText && Str(input, "new_string") is { } newText)
        {
            Diff = DiffView.FromReplacement(oldText, newText);
        }
        else if (name == "Write" && Str(input, "content") is { } content)
        {
            Diff = DiffView.FromNewFile(content);
        }
    }

    public string ToolUseId { get; }

    public string Name { get; }

    public JsonObject Input { get; }

    public string Summary { get; }

    public string Icon => Name switch
    {
        "Read" => "▤",
        "Write" or "Edit" or "NotebookEdit" => "✎",
        "Bash" => "❯",
        "Grep" or "Glob" => "⌕",
        "WebFetch" or "WebSearch" => "◍",
        "Agent" or "Task" => "◈",
        "Skill" => "✦",
        "AskUserQuestion" => "?",
        "ExitPlanMode" => "☰",
        _ => "•",
    };

    /// <summary>The card's icon: the key of a vector icon in App.axaml (DESIGN.md §5).</summary>
    public string IconKey => ToolIcons.KeyFor(Name);

    /// <summary>Edit, Write and the other file tools, which can be opened in the diff view (DESIGN.md §5, §8).</summary>
    public bool IsFileChange => Core.Diffs.ChangedFiles.IsFileTool(Name);

    /// <summary>A file change that went through, so there's something to show in the diff view.</summary>
    public bool CanOpenDiff => IsFileChange && IsComplete && !IsError;

    /// <summary>For Bash: the command, shown in full when expanded.</summary>
    public string? Command { get; }

    public bool IsBash => Command is not null;

    /// <summary>The full input, for tools without a better view.</summary>
    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff), nameof(DiffStats))]
    public partial DiffView? Diff { get; set; }

    public bool HasDiff => Diff is { Lines.Count: > 0 };

    public string? DiffStats => Diff?.Stats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string? Output { get; set; }

    public bool HasOutput => !string.IsNullOrEmpty(Output);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultSummary))]
    public partial string? ResultSummary { get; set; }

    public bool HasResultSummary => !string.IsNullOrEmpty(ResultSummary);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenDiff))]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenDiff))]
    public partial bool IsComplete { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool CanExpand => HasDetail || HasDiff || HasOutput || IsBash;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>Fills in the result, using Claude Code's structured <c>tool_use_result</c> where it has one.</summary>
    public void ApplyResult(string text, bool isError, JsonNode? toolUseResult)
    {
        IsComplete = true;
        IsError = isError;
        if (toolUseResult is JsonObject result)
        {
            if (result["structuredPatch"] is JsonArray { Count: > 0 } patch)
            {
                Diff = DiffView.FromStructuredPatch(patch);
            }
            else if (Name == "Write" && result["type"]?.GetValue<string>() == "create" && Str(result, "content") is { } content)
            {
                Diff = DiffView.FromNewFile(content);
            }
            if (Name == "Bash")
            {
                var stdout = Str(result, "stdout") ?? "";
                var stderr = Str(result, "stderr") ?? "";
                Output = string.Join('\n', new[] { stdout, stderr }.Where(s => s.Length > 0));
                ResultSummary = result["interrupted"]?.GetValue<bool>() == true ? "Interrupted" : FirstLine(Output) ?? FirstLine(text);
                OnPropertyChanged(nameof(CanExpand));
                return;
            }
        }
        ResultSummary = HasDiff && !isError ? null : FirstLine(text);
        if (!HasDiff && Name is not ("Read" or "Agent" or "Task"))
        {
            Output = text;
        }
        OnPropertyChanged(nameof(CanExpand));
    }

    /// <summary>The most telling input field, such as the file path or command.</summary>
    public static string Summarize(string name, JsonObject input)
    {
        var summary = name switch
        {
            "Read" when Str(input, "file_path") is { } path && input["offset"] is not null
                => $"{path} (from line {input["offset"]})",
            "Grep" when Str(input, "pattern") is { } pattern
                => Str(input, "path") is { } where ? $"{pattern}  in {where}" : pattern,
            "Agent" or "Task" => Str(input, "description"),
            "AskUserQuestion" => (input["questions"] as JsonArray)?.OfType<JsonObject>().Select(q => Str(q, "question")).FirstOrDefault(q => q is not null),
            "ExitPlanMode" => "Plan ready for review",
            _ => null,
        };
        if (summary is null)
        {
            foreach (var key in new[] { "file_path", "notebook_path", "command", "pattern", "path", "url", "query", "skill", "description", "prompt" })
            {
                if (Str(input, key) is { } value)
                {
                    summary = value;
                    break;
                }
            }
        }
        summary = (summary ?? "").ReplaceLineEndings(" ");
        return summary.Length > 140 ? summary[..137] + "…" : summary;
    }

    private static string? DetailText(string name, JsonObject input) => name switch
    {
        "Bash" or "Edit" or "Write" or "Read" => null,
        "Agent" or "Task" => Str(input, "prompt"),
        "ExitPlanMode" => Str(input, "plan"),
        "AskUserQuestion" => string.Join("\n\n", (input["questions"] as JsonArray ?? []).OfType<JsonObject>().Select(q =>
            $"{Str(q, "question")}\n" + string.Join("\n", (q["options"] as JsonArray ?? []).OfType<JsonObject>().Select(o => $"  • {Str(o, "label")}")))),
        "WebFetch" => Str(input, "prompt") is { } prompt ? $"{Str(input, "url")}\n\n{prompt}" : null,
        _ => input.Count > 0 ? input.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null,
    };

    protected static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');
        var first = (end >= 0 ? line[..end] : line).ToString();
        return first.Length > 160 ? first[..157] + "…" : first;
    }
}

/// <summary>A subagent: a collapsible group holding that agent's own text and tool calls (DESIGN.md §5).</summary>
public sealed partial class SubagentItem : ToolUseItem
{
    public SubagentItem(string toolUseId, string name, JsonObject input) : base(toolUseId, name, input)
    {
        AgentType = Str(input, "subagent_type") ?? "agent";
    }

    public string AgentType { get; }

    public ObservableCollection<ConversationItem> Items { get; } = [];
}

public enum NoteKind
{
    Info,
    Warning,
    Error,
}

/// <summary>A system note: a model change, an error, a retry, the process exiting.</summary>
public sealed partial class NoteItem(string text, NoteKind kind) : ConversationItem
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;

    public NoteKind Kind { get; } = kind;

    public bool IsError => Kind == NoteKind.Error;

    public bool IsWarning => Kind == NoteKind.Warning;
}

/// <summary>The small footer after each turn: duration, tokens and model (DESIGN.md §5).</summary>
public sealed class TurnSummaryItem(string text) : ConversationItem
{
    public string Text { get; } = text;
}
