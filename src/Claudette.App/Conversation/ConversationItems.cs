using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>
/// A user message or an assistant reply. It has a Copy button and shows when it was sent, on hover (DESIGN.md §5,
/// "Copy and times").
/// </summary>
public abstract partial class MessageItem : ConversationItem
{
    private TimeProvider? _clock;

    /// <summary>When it was sent: live, as it was built; restored, from its transcript entry. Null when unknown.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    public bool HasTime => SentAt is not null && _clock is not null;

    /// <summary>Short, in the current culture: "14:05" today, "Mon 14:05" in the last week, else the date and time.</summary>
    public string? TimeText => SentAt is { } sent && _clock is { } clock ? MessageTimes.Short(sent, clock) : null;

    /// <summary>The full date and time, for the tooltip.</summary>
    public string? TimeTip => SentAt is { } sent && _clock is { } clock ? MessageTimes.Full(sent, clock) : null;

    /// <summary>What <b>Copy message</b> puts on the clipboard.</summary>
    public abstract string CopyText { get; }

    /// <summary>Just copied: the button says "Copied" for a moment.</summary>
    [ObservableProperty]
    public partial bool IsCopied { get; set; }

    /// <summary>Sets when the message was sent; <paramref name="clock"/> says what "today" is when it's shown.</summary>
    public void Stamp(DateTimeOffset? sentAt, TimeProvider clock)
    {
        SentAt = sentAt;
        _clock = clock;
        RefreshTime();
    }

    /// <summary>"Today" moved on, for example past midnight: the short time may need its day now.</summary>
    public void RefreshTime()
    {
        OnPropertyChanged(nameof(SentAt));
        OnPropertyChanged(nameof(HasTime));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(TimeTip));
    }
}

/// <summary>How a message's time reads, in the clock's time zone and the current culture (DESIGN.md §5).</summary>
public static class MessageTimes
{
    /// <summary>"14:05" today, "Mon 14:05" in the six days before, else the short date and time.</summary>
    public static string Short(DateTimeOffset sent, TimeProvider clock)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(sent, clock.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone).Date;
        var time = local.ToString("t", culture);
        return local.Date == today ? time
            : local.Date < today && local.Date > today.AddDays(-7) ? $"{local.ToString("ddd", culture)} {time}"
            : local.ToString("g", culture);
    }

    /// <summary>The long date and the time, for the tooltip.</summary>
    public static string Full(DateTimeOffset sent, TimeProvider clock) =>
        TimeZoneInfo.ConvertTime(sent, clock.LocalTimeZone).ToString("f", CultureInfo.CurrentCulture);
}

public sealed class UserMessageItem(string text, string? suffixText = null, bool isCheckIn = false) : MessageItem
{
    public string Text { get; } = text;

    /// <summary>Quick suffixes appended to the message, shown in a lighter style (DESIGN.md §5, "Quick suffixes").</summary>
    public string? SuffixText { get; } = suffixText;

    public bool HasSuffix => !string.IsNullOrEmpty(SuffixText);

    /// <summary>The message as it was sent: its text, then its quick suffixes after a blank line.</summary>
    public override string CopyText => !HasSuffix ? Text : Text.Length == 0 ? SuffixText! : $"{Text}\n\n{SuffixText}";

    /// <summary>Sent by Claudette as an automatic check-in (DESIGN.md §5, "Check-ins on long turns").</summary>
    public bool IsCheckIn { get; } = isCheckIn;

    /// <summary>Attached images, shown as thumbnails (DESIGN.md §5, "Attachments").</summary>
    public IReadOnlyList<MessageImage> Images { get; init; } = [];

    public bool HasImages => Images.Count > 0;
}

/// <summary>Assistant text, streamed in as Markdown.</summary>
public sealed partial class AssistantTextItem : MessageItem
{
    public ObservableStringBuilder Markdown { get; } = new();

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    public string Text => Markdown.ToString();

    /// <summary>The reply's Markdown, as Claude wrote it.</summary>
    public override string CopyText => Text;

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

    /// <summary>The summary in full, for its tooltip: a command keeps its line breaks.</summary>
    public string FullSummary => Name is "Bash" or "PowerShell" && Str(Input, "command") is { } command
        ? command.Trim()
        : Summarize(Name, Input, int.MaxValue);

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
    public virtual void ApplyResult(string text, bool isError, JsonNode? toolUseResult)
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
    public static string Summarize(string name, JsonObject input, int maxLength = 140)
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
        return summary.Length > maxLength ? summary[..(maxLength - 3)] + "…" : summary;
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

    protected static string? FirstLine(string? text)
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

    /// <summary>Launched in the background: its call returns at once, and it keeps running (DESIGN.md §18).</summary>
    [ObservableProperty]
    public partial bool IsBackground { get; private set; }

    /// <summary>Stopped, by the user or an interrupt: neither done nor failed, so its dot is muted.</summary>
    public bool IsStopped { get; private set; }

    /// <summary>Finished and returned its report: the green dot.</summary>
    public bool IsSucceeded => IsComplete && !IsError && !IsStopped;

    public override void ApplyResult(string text, bool isError, JsonNode? toolUseResult)
    {
        if (!isError && toolUseResult is JsonObject result && Str(result, "status") is "async_launched" or "remote_launched")
        {
            // Not finished: a task notification says when it is.
            IsBackground = true;
            ResultSummary = "Running in the background";
            return;
        }
        base.ApplyResult(text, isError, toolUseResult);
        if (!isError && FirstLine(Report(text, toolUseResult)) is { } report)
        {
            ResultSummary = report;
        }
        OnPropertyChanged(nameof(IsSucceeded));
    }

    /// <summary>How it ended, as the agent map sees it, so the group's status dot and result line agree with the map.</summary>
    internal void ShowEnded(AgentStatus status, string? report)
    {
        IsComplete = true;
        IsError = status == AgentStatus.Failed;
        IsStopped = status == AgentStatus.Stopped;
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(IsSucceeded));
        ResultSummary = status switch
        {
            AgentStatus.Stopped => "Stopped",
            _ => FirstLine(report) ?? ResultSummary,
        };
    }

    private const string HandBackMarker = "The report follows:";

    /// <summary>
    /// The report a subagent handed back. Claude Code puts it in <c>tool_use_result.content</c>; nested subagents'
    /// results have no <c>tool_use_result</c>, so their tool result text is read instead, without the frame Claude
    /// Code puts around it ("[Subagent hand-back] … The report follows:", indented, then an <c>agentId</c> and
    /// <c>&lt;usage&gt;</c> trailer). Null when the frame is there but can't be read.
    /// </summary>
    public static string? Report(string text, JsonNode? toolUseResult)
    {
        if (toolUseResult is JsonObject result && result["content"] is JsonArray content)
        {
            var joined = string.Join("\n\n", content.OfType<JsonObject>().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")).OfType<string>());
            if (joined.Length > 0)
            {
                return joined;
            }
        }
        if (!text.StartsWith("[Subagent hand-back]", StringComparison.Ordinal))
        {
            return text;
        }
        var start = text.IndexOf(HandBackMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        var lines = text[(start + HandBackMarker.Length)..].Split('\n').ToList();
        var usage = lines.FindIndex(l => l.StartsWith("<usage>", StringComparison.Ordinal));
        if (usage >= 0)
        {
            lines.RemoveRange(usage, lines.Count - usage);
        }
        if (lines.Count > 0 && lines[^1].StartsWith("agentId: ", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }
        // The harness indents every line of the report by two spaces.
        return string.Join('\n', lines.Select(l => l.StartsWith("  ", StringComparison.Ordinal) ? l[2..] : l)).Trim();
    }
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

    /// <summary>An address to open from the note, such as the tab's session in the Claude app (DESIGN.md §18).</summary>
    public string? Link { get; init; }

    public bool HasLink => Link is not null;

    public bool IsError => Kind == NoteKind.Error;

    public bool IsWarning => Kind == NoteKind.Warning;
}

/// <summary>
/// A message Claudette doesn't know and skipped, shown only while protocol logging is on (DESIGN.md §16): a collapsed
/// row that expands to the raw JSON.
/// </summary>
public sealed partial class UnsupportedMessageItem(string messageType, string json) : ConversationItem
{
    public string Title => $"Unsupported message from Claude Code: {messageType}";

    public string Json { get; } = json;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>The small footer after each turn: duration, tokens and model (DESIGN.md §5).</summary>
public sealed class TurnSummaryItem(string text) : ConversationItem
{
    public string Text { get; } = text;
}
