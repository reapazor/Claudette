using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core;
using Claudette.Core.Protocol;
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

public sealed partial class UserMessageItem(string text, string? suffixText = null, bool isCheckIn = false, bool isAutoContinue = false) : MessageItem
{
    public string Text { get; } = text;

    /// <summary>
    /// The prompt's transcript id, once Claude Code has echoed it back (or from the transcript): the point its files can
    /// be put back to (DESIGN.md §5, "Rewind and branch").
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreFiles))]
    public partial string? Uuid { get; set; }

    /// <summary>The id Claudette sent the prompt with, which Claude Code echoes back (DESIGN.md §13, "Wire format").</summary>
    public string? SentId { get; set; }

    /// <summary>
    /// Sent while Claude worked, and waiting its turn: Claude Code hasn't taken it yet (DESIGN.md §5, "Queued messages").
    /// Its echo ends the wait.
    /// </summary>
    [ObservableProperty]
    public partial bool IsQueued { get; set; }

    /// <summary>
    /// The conversation entry just before this prompt: resuming there leaves the prompt out. Null for the first prompt,
    /// before which there's nothing to keep.
    /// </summary>
    public string? ResumeAt { get; set; }

    /// <summary>Restore files to before this message: it has a checkpoint to go back to.</summary>
    public bool CanRestoreFiles => Uuid is not null;

    /// <summary>Quick suffixes appended to the message, shown in a lighter style (DESIGN.md §5, "Quick suffixes").</summary>
    public string? SuffixText { get; } = suffixText;

    public bool HasSuffix => !string.IsNullOrEmpty(SuffixText);

    /// <summary>The message as it was sent: its text, then its quick suffixes after a blank line.</summary>
    public override string CopyText => !HasSuffix ? Text : Text.Length == 0 ? SuffixText! : $"{Text}\n\n{SuffixText}";

    /// <summary>Sent by Claudette as an automatic check-in (DESIGN.md §5, "Check-ins on long turns").</summary>
    public bool IsCheckIn { get; } = isCheckIn;

    /// <summary>Sent by Claudette once a usage limit reset (DESIGN.md §6, "Continuing after a limit resets").</summary>
    public bool IsAutoContinue { get; } = isAutoContinue;

    /// <summary>The label over a message Claudette sent by itself, or null for one the user sent.</summary>
    public string? AutomaticLabel => IsCheckIn ? "Automatic check-in" : IsAutoContinue ? "Automatic continue after the usage limit reset" : null;

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
    private readonly System.Text.StringBuilder _text = new();
    private string? _textAsString = "";

    /// <summary>How long <see cref="ShownText"/> was when the view was last told it changed.</summary>
    private int _shownLength;

    /// <summary>
    /// The thinking so far. It streams in small pieces, so it's kept in a builder and made into a string only when read,
    /// rather than copied whole for every piece.
    /// </summary>
    public string Text
    {
        get => _textAsString ??= _text.ToString();
        set
        {
            _text.Clear().Append(value);
            TextChanged(force: true);
        }
    }

    public void Append(string text)
    {
        if (text.Length > 0)
        {
            _text.Append(text);
            TextChanged(force: false);
        }
    }

    /// <summary>
    /// What the view shows: the text while the row is expanded, else nothing, so collapsed thinking is never made into a
    /// string. While it streams in, the view hears of it as it grows by an eighth (at least 256 characters) rather than
    /// with every piece, and all of it once it's done.
    /// </summary>
    public string? ShownText => IsExpanded ? Text : null;

    private void TextChanged(bool force)
    {
        _textAsString = null;
        if (HasText != _saidHasText)
        {
            _saidHasText = HasText;
            OnPropertyChanged(nameof(HasText));
        }
        if (force || _shownLength == 0 || _text.Length - _shownLength >= Math.Max(256, _shownLength / 8))
        {
            ShowText();
        }
    }

    private bool _saidHasText;

    private void ShowText()
    {
        _shownLength = _text.Length;
        OnPropertyChanged(nameof(Text));
        if (IsExpanded)
        {
            OnPropertyChanged(nameof(ShownText));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial bool IsStreaming { get; set; } = true;

    // All of it, once it's done.
    partial void OnIsStreamingChanged(bool value) => ShowText();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownText))]
    public partial bool IsExpanded { get; set; }

    public bool HasText => _text.Length > 0;

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
        // A copy without its parent: the input is a node of the whole message, which it would otherwise keep alive.
        Input = input.Parent is null ? input : input.DeepClone().AsObject();
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
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(ShownOutput), nameof(IsOutputCut), nameof(ShowAllOutputText))]
    public partial string? Output { get; set; }

    public bool HasOutput => !string.IsNullOrEmpty(Output);

    /// <summary>How much of a long output the card shows until **Show all** (a whole file Read, a big Grep).</summary>
    public const int ShownOutputLimit = 20_000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownOutput), nameof(IsOutputCut))]
    public partial bool ShowsAllOutput { get; private set; }

    /// <summary>
    /// The output as the card shows it: the start of a long one, since laying out a hundred kilobytes of text in one block
    /// is slow even while most of it is scrolled out of sight.
    /// </summary>
    public string? ShownOutput => IsOutputCut ? Output![..ShownOutputLimit] : Output;

    public bool IsOutputCut => !ShowsAllOutput && Output is { Length: > ShownOutputLimit };

    public string ShowAllOutputText => Output is { } output
        ? $"Show all ({(output.Length + 1023) / 1024:N0} KB)"
        : "Show all";

    [RelayCommand]
    private void ShowAllOutput() => ShowsAllOutput = true;

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
            else if (Name == "Write" && Str(result, "type") == "create" && Str(result, "content") is { } content)
            {
                Diff = DiffView.FromNewFile(content);
            }
            if (Name == "Bash")
            {
                var stdout = Str(result, "stdout") ?? "";
                var stderr = Str(result, "stderr") ?? "";
                Output = string.Join('\n', new[] { stdout, stderr }.Where(s => s.Length > 0));
                ResultSummary = BashSummary(result) ?? FirstLine(Output) ?? FirstLine(text);
                GitChips = Conversation.GitChips.From(result.GetObject("gitOperation"));
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

    /// <summary>What git did, read from a Bash result's <c>gitOperation</c>: "Committed 1a2b3c4 on main", "Opened PR #42".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitChips))]
    public partial IReadOnlyList<GitChip> GitChips { get; private set; } = [];

    public bool HasGitChips => GitChips.Count > 0;

    /// <summary>How a Bash command ended, when its result says more than its output's first line (DESIGN.md §5).</summary>
    private static string? BashSummary(JsonObject result)
    {
        if (result.GetDouble("timedOutAfterMs") is { } timedOut)
        {
            return $"Reached its {Formats.Elapsed(TimeSpan.FromMilliseconds(timedOut))} time limit; carries on in the background";
        }
        if (result.GetString("backgroundTaskId") is not null)
        {
            return result.GetBool("backgroundedByUser") == true ? "Moved to the background" : "Running in the background";
        }
        return result.GetBool("interrupted") == true ? "Interrupted" : null;
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

    protected static string? Str(JsonObject obj, string name) => obj.GetString(name);

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

/// <summary>How a hook run ended, or that it hasn't yet.</summary>
public enum HookRunState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One run of a hook from the user's or project's settings (DESIGN.md §5, "Hook runs"): a compact row, which expands to
/// what the hook printed. Hook names and output are the hook's own: untrusted text.
/// </summary>
public sealed partial class HookRunItem(string hookId, string hookName, string hookEvent) : ConversationItem
{
    /// <summary>Most of a hook's output kept for the row; a hook that prints more is cut, as a tool's output is.</summary>
    public const int OutputLimit = 20_000;

    public string HookId { get; } = hookId;

    /// <summary>The hook's name, such as <c>UserPromptSubmit</c> or the matcher it runs for.</summary>
    public string HookName { get; } = hookName;

    /// <summary>The event that ran it, such as <c>PreToolUse</c>.</summary>
    public string HookEvent { get; } = hookEvent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFailed), nameof(Title), nameof(StatusText))]
    public partial HookRunState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string Output { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial int? ExitCode { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool IsRunning => State == HookRunState.Running;

    public bool IsFailed => State == HookRunState.Failed;

    public bool HasOutput => Output.Length > 0;

    /// <summary>"PreToolUse hook" or "PreToolUse hook (Bash)", for the row.</summary>
    public string Title => HookName.Length > 0 && HookName != HookEvent ? $"{HookEvent} hook ({HookName})" : $"{HookEvent} hook";

    public string StatusText => State switch
    {
        HookRunState.Running => "running…",
        HookRunState.Failed => ExitCode is { } code ? $"failed (exit code {code})" : "failed",
        HookRunState.Cancelled => "cancelled",
        _ => "done",
    };

    internal void Append(string text)
    {
        if (text.Length == 0 || Output.Length >= OutputLimit)
        {
            return;
        }
        var joined = Output + text;
        Output = joined.Length > OutputLimit ? joined[..OutputLimit] + "\n…" : joined;
    }
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

    /// <summary>The footer for a turn's result; null when the result says none of those.</summary>
    /// <param name="modelName">Turns a model id into a display name.</param>
    internal static TurnSummaryItem? For(ResultMessage result, Func<string?, string?> modelName)
    {
        var parts = new List<string>();
        if (result.DurationMs is { } ms)
        {
            parts.Add(ms >= 60_000 ? $"{(int)(ms / 60_000)}m {ms % 60_000 / 1000:0}s" : $"{ms / 1000:0.#}s");
        }
        if (result.Usage is { } usage)
        {
            var input = (Number(usage["input_tokens"]) ?? 0) + (Number(usage["cache_creation_input_tokens"]) ?? 0) + (Number(usage["cache_read_input_tokens"]) ?? 0);
            var output = Number(usage["output_tokens"]) ?? 0;
            parts.Add($"{Tokens(input)} in · {Tokens(output)} out");
        }
        if (result.ModelUsage is { Count: > 0 } models)
        {
            parts.Add(string.Join(", ", models.Select(m => modelName(m.Key) ?? m.Key)));
        }
        return parts.Count > 0 ? new TurnSummaryItem(string.Join(" · ", parts)) : null;
    }

    private static string Tokens(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString(),
    };

    private static long? Number(JsonNode? node) => node.AsWholeNumber();
}
