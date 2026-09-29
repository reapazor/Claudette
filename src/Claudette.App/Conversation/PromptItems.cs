using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Claudette.App.ViewModels;
using LiveMarkdown.Avalonia;

namespace Claudette.App.Conversation;

public enum PermissionState
{
    Pending,
    Allowed,
    Denied,
    Cancelled,
}

/// <summary>
/// Something Claude Code is waiting on the user for (DESIGN.md §7): a permission prompt, a clarifying question or a
/// plan to approve. All of them arrive as <c>can_use_tool</c> requests.
/// </summary>
public abstract partial class PromptItem(PermissionRequest request) : ConversationItem
{
    public PermissionRequest Request { get; } = request;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(HasOutcome))]
    public partial PermissionState State { get; set; } = PermissionState.Pending;

    public bool IsPending => State == PermissionState.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    public partial string Outcome { get; set; } = "";

    public bool HasOutcome => !IsPending && Outcome.Length > 0;

    /// <summary>The subagent that's asking, when it isn't the main agent (DESIGN.md §18, "Agent map").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAsker))]
    public partial string? Asker { get; set; }

    public bool HasAsker => Asker is not null;

    /// <summary>Raised when the user answers, so the tab can update its "needs input" status.</summary>
    public event EventHandler? Answered;

    /// <summary>Ctrl/Cmd+Enter: the safe "yes" for this prompt. False when there isn't one.</summary>
    public abstract bool TryAcceptFromKeyboard();

    /// <summary>Ctrl/Cmd+Backspace: the "no" for this prompt.</summary>
    public abstract bool TryDeclineFromKeyboard();

    /// <summary>What a prompt Claude Code withdrew says, unless it knows more.</summary>
    public const string WithdrawnOutcome = "No longer needed";

    /// <summary>
    /// Claude Code withdrew the request, or the session ended. <paramref name="outcome"/> says why when it's known, such
    /// as "Answered in the Claude app" (DESIGN.md §18, "Remote Control").
    /// </summary>
    public void Cancel(string? outcome = null)
    {
        if (IsPending)
        {
            Outcome = outcome ?? WithdrawnOutcome;
            State = PermissionState.Cancelled;
        }
    }

    protected void Finish(PermissionState state, string outcome)
    {
        Outcome = outcome;
        State = state;
        Answered?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// An inline permission prompt: the tool and its input, then Allow, Always allow (with the exact rule, editable) or
/// Deny with an optional message (DESIGN.md §7).
/// </summary>
public sealed partial class PermissionItem : PromptItem
{
    private static readonly string[] EditTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    public PermissionItem(PermissionRequest request) : base(request)
    {
        var input = request.Input;
        var name = request.DisplayName ?? request.ToolName;
        var path = Str(input, "file_path") ?? Str(input, "notebook_path");
        Title = request.ToolName switch
        {
            "Bash" => "Allow this command?",
            "Write" when path is not null && !File.Exists(path) => $"Allow creating {Path.GetFileName(path)}?",
            _ when EditTools.Contains(request.ToolName) && path is not null => $"Allow editing {Path.GetFileName(path)}?",
            "WebFetch" => "Allow fetching this page?",
            _ => $"Allow {name}?",
        };
        Command = request.ToolName == "Bash" ? Str(input, "command") : null;
        Detail = Command is not null ? null : ToolUseItem.Summarize(request.ToolName, input) is { Length: > 0 } summary ? summary : null;
        Description = request.Description is { } description && description != Detail && description != Path.GetFileName(path ?? "") ? description : null;
        Reason = request.DecisionReason;
        if (request.ToolName == "Edit" && Str(input, "old_string") is { } oldText && Str(input, "new_string") is { } newText)
        {
            Diff = DiffView.FromReplacement(oldText, newText);
        }
        else if (request.ToolName == "Write" && Str(input, "content") is { } content)
        {
            Diff = DiffView.FromNewFile(content);
        }

        SuggestedRules = request.SuppressAlwaysAllowRule ? [] : request.SuggestedRules;
        RuleText = PermissionRule.Format(SuggestedRules);
        ModeAction = request.SuggestedMode switch
        {
            "acceptEdits" => "Allow all edits this session",
            "bypassPermissions" or null => null,
            { } mode => $"Allow and switch to {PermissionModeInfo.Label(mode)} mode",
        };
    }

    public string Title { get; }

    /// <summary>For Bash: the command, in full.</summary>
    public string? Command { get; }

    public bool HasCommand => Command is not null;

    /// <summary>The file path, URL or other one-line summary of the input.</summary>
    public string? Detail { get; }

    public bool HasDetail => Detail is not null;

    /// <summary>Claude's description of what the tool call does, when it adds something.</summary>
    public string? Description { get; }

    public bool HasDescription => Description is not null;

    /// <summary>Why Claude Code asked, when it says.</summary>
    public string? Reason { get; }

    public bool HasReason => Reason is not null;

    /// <summary>A preview of an edit, before it happens.</summary>
    public DiffView? Diff { get; }

    public bool HasDiff => Diff is { Lines.Count: > 0 };

    // ---- Always allow (DESIGN.md §7) -------------------------------------------------------------------------

    public IReadOnlyList<PermissionRule> SuggestedRules { get; }

    public bool CanAlwaysAllow => SuggestedRules.Count > 0;

    [ObservableProperty]
    public partial bool IsEditingRule { get; set; }

    /// <summary>The rule to save, as Claude Code writes it. The user can make it broader or narrower.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleError), nameof(HasRuleError))]
    [NotifyCanExecuteChangedFor(nameof(SaveRuleCommand), nameof(AllowForSessionCommand))]
    public partial string RuleText { get; set; } = "";

    public string? RuleError => PermissionRule.TryParseList(RuleText, out _) ? null : "Write a rule like Bash(npm test:*), or several separated by commas.";

    public bool HasRuleError => IsEditingRule && RuleError is not null;

    /// <summary>A mode switch Claude Code suggests instead of a rule, such as accepting all edits for the session.</summary>
    public string? ModeAction { get; }

    public bool HasModeAction => ModeAction is not null;

    // ---- Deny with a message -------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsWritingDenyMessage { get; set; }

    [ObservableProperty]
    public partial string DenyMessage { get; set; } = "";

    [RelayCommand]
    private void Allow()
    {
        if (IsPending)
        {
            Request.Allow();
            Finish(PermissionState.Allowed, "Allowed");
        }
    }

    [RelayCommand]
    private void StartAlwaysAllow()
    {
        if (IsPending && CanAlwaysAllow)
        {
            IsWritingDenyMessage = false;
            IsEditingRule = true;
            OnPropertyChanged(nameof(HasRuleError));
        }
    }

    [RelayCommand]
    private void CancelAlwaysAllow() => IsEditingRule = false;

    /// <summary>Allows, and saves the rule to <c>.claude/settings.local.json</c>. Claude Code writes the file.</summary>
    [RelayCommand(CanExecute = nameof(IsRuleValid))]
    private void SaveRule()
    {
        if (IsPending && PermissionRule.TryParseList(RuleText, out var rules))
        {
            Request.AllowAlways(rules, PermissionRequest.LocalSettings);
            IsEditingRule = false;
            Finish(PermissionState.Allowed, $"Allowed. Saved {PermissionRule.Format(rules)} to .claude/settings.local.json");
        }
    }

    /// <summary>"Allow for this session only": the same rule, but nothing is saved.</summary>
    [RelayCommand(CanExecute = nameof(IsRuleValid))]
    private void AllowForSession()
    {
        if (IsPending && PermissionRule.TryParseList(RuleText, out var rules))
        {
            Request.AllowAlways(rules, PermissionRequest.Session);
            IsEditingRule = false;
            Finish(PermissionState.Allowed, $"Allowed {PermissionRule.Format(rules)} for this session");
        }
    }

    private bool IsRuleValid() => PermissionRule.TryParseList(RuleText, out _);

    [RelayCommand]
    private void AllowWithMode()
    {
        if (IsPending && Request.SuggestedMode is { } mode)
        {
            Request.AllowAndSetMode(mode);
            Finish(PermissionState.Allowed, mode == "acceptEdits" ? "Allowed. Edits are accepted for the rest of this session" : $"Allowed, and switched to {PermissionModeInfo.Label(mode)} mode");
        }
    }

    [RelayCommand]
    private void Deny()
    {
        if (IsPending)
        {
            Request.Deny("The user denied this action.");
            Finish(PermissionState.Denied, "Denied");
        }
    }

    [RelayCommand]
    private void StartDenyWithMessage()
    {
        if (IsPending)
        {
            IsEditingRule = false;
            IsWritingDenyMessage = true;
        }
    }

    [RelayCommand]
    private void CancelDenyWithMessage() => IsWritingDenyMessage = false;

    /// <summary>Denies and tells Claude what to do instead.</summary>
    [RelayCommand]
    private void SendDenyMessage()
    {
        if (!IsPending)
        {
            return;
        }
        var message = DenyMessage.Trim();
        if (message.Length == 0)
        {
            Deny();
            return;
        }
        Request.Deny($"The user denied this action and said: {message}");
        IsWritingDenyMessage = false;
        Finish(PermissionState.Denied, $"Denied: {message}");
    }

    public override bool TryAcceptFromKeyboard()
    {
        // Claude Code marks some requests so a stray keystroke can't approve them.
        if (!IsPending || Request.DefaultToNo)
        {
            return false;
        }
        Allow();
        return true;
    }

    public override bool TryDeclineFromKeyboard()
    {
        if (!IsPending)
        {
            return false;
        }
        Deny();
        return true;
    }

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}

/// <summary>One option of a clarifying question.</summary>
public sealed partial class QuestionOption(string label, string? description, string group) : ObservableObject
{
    public string Label { get; } = label;

    public string? Description { get; } = description;

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>Radio button group, so single-choice options exclude each other.</summary>
    public string Group { get; } = group;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One question from <c>AskUserQuestion</c>, with its options and an "Other" answer.</summary>
public sealed partial class QuestionEntry : ObservableObject
{
    public QuestionEntry(JsonObject question, Action changed)
    {
        _changed = changed;
        Text = question["question"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : "";
        Header = question["header"] is JsonValue h && h.GetValueKind() == JsonValueKind.String ? h.GetValue<string>() : null;
        MultiSelect = question["multiSelect"] is JsonValue m && m.GetValueKind() == JsonValueKind.True;
        var group = Guid.NewGuid().ToString("N");
        foreach (var option in (question["options"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var label = option["label"] is JsonValue l && l.GetValueKind() == JsonValueKind.String ? l.GetValue<string>() : null;
            if (label is null)
            {
                continue;
            }
            var description = option["description"] is JsonValue d && d.GetValueKind() == JsonValueKind.String ? d.GetValue<string>() : null;
            var entry = new QuestionOption(label, description, group);
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QuestionOption.IsSelected))
                {
                    if (entry.IsSelected && !MultiSelect)
                    {
                        OtherText = "";
                    }
                    changed();
                }
            };
            Options.Add(entry);
        }
    }

    private readonly Action _changed;

    public string Text { get; }

    public string? Header { get; }

    public bool HasHeader => !string.IsNullOrEmpty(Header);

    public bool MultiSelect { get; }

    public string Hint => MultiSelect ? "Pick any that apply" : "Pick one";

    public ObservableCollection<QuestionOption> Options { get; } = [];

    /// <summary>The user's own answer, instead of (or, for multi-select, as well as) the options.</summary>
    [ObservableProperty]
    public partial string OtherText { get; set; } = "";

    partial void OnOtherTextChanged(string value)
    {
        if (!MultiSelect && value.Trim().Length > 0)
        {
            foreach (var option in Options.Where(o => o.IsSelected))
            {
                option.IsSelected = false;
            }
        }
        _changed();
    }

    /// <summary>The answer Claude receives: the chosen labels, joined with ", ", or the user's own text.</summary>
    public string? Answer
    {
        get
        {
            var parts = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (OtherText.Trim() is { Length: > 0 } other)
            {
                parts.Add(other);
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
    }
}

/// <summary>
/// Claude's clarifying questions (<c>AskUserQuestion</c>). The answers go back as the tool's input, keyed by
/// question text (Agent SDK "Handle clarifying questions").
/// </summary>
public sealed partial class QuestionItem : PromptItem
{
    public QuestionItem(PermissionRequest request) : base(request)
    {
        foreach (var question in (request.Input["questions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            Questions.Add(new QuestionEntry(question, () => SubmitCommand.NotifyCanExecuteChanged()));
        }
    }

    public ObservableCollection<QuestionEntry> Questions { get; } = [];

    public string Title => Questions.Count == 1 ? "Claude has a question" : "Claude has some questions";

    private bool CanSubmit() => IsPending && Questions.Count > 0 && Questions.All(q => q.Answer is not null);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        if (!CanSubmit())
        {
            return;
        }
        var answers = new JsonObject();
        foreach (var question in Questions)
        {
            answers[question.Text] = question.Answer;
        }
        var input = (JsonObject)Request.Input.DeepClone();
        input["answers"] = answers;
        Request.Allow(input);
        Finish(PermissionState.Allowed, string.Join("  ·  ", Questions.Select(q => $"{q.Header ?? q.Text}: {q.Answer}")));
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (IsPending)
        {
            Request.Deny("The user dismissed the questions without answering. Ask again later or continue with your best judgment.");
            Finish(PermissionState.Denied, "Dismissed without answering");
        }
    }

    public override bool TryAcceptFromKeyboard()
    {
        if (!CanSubmit())
        {
            return false;
        }
        Submit();
        return true;
    }

    public override bool TryDeclineFromKeyboard()
    {
        if (!IsPending)
        {
            return false;
        }
        Dismiss();
        return true;
    }
}

/// <summary>
/// Claude finished planning (<c>ExitPlanMode</c>) and wants to start changing things: approve with edits accepted,
/// approve with edits still asked for, or keep planning with feedback.
/// </summary>
public sealed partial class PlanItem : PromptItem
{
    public PlanItem(PermissionRequest request) : base(request)
    {
        if (request.Input["plan"] is JsonValue plan && plan.GetValueKind() == JsonValueKind.String)
        {
            Plan.Append(plan.GetValue<string>());
        }
    }

    public ObservableStringBuilder Plan { get; } = new();

    public bool HasPlan => Plan.ToString().Length > 0;

    [ObservableProperty]
    public partial bool IsWritingFeedback { get; set; }

    [ObservableProperty]
    public partial string Feedback { get; set; } = "";

    [RelayCommand]
    private void ApproveAcceptingEdits() => Approve("acceptEdits", "Approved. Edits are accepted automatically");

    [RelayCommand]
    private void ApproveAskingForEdits() => Approve("default", "Approved. Claude will ask before each edit");

    private void Approve(string mode, string outcome)
    {
        if (IsPending)
        {
            Request.AllowAndSetMode(mode);
            Finish(PermissionState.Allowed, outcome);
        }
    }

    [RelayCommand]
    private void StartFeedback()
    {
        if (IsPending)
        {
            IsWritingFeedback = true;
        }
    }

    [RelayCommand]
    private void CancelFeedback() => IsWritingFeedback = false;

    [RelayCommand]
    private void KeepPlanning()
    {
        if (!IsPending)
        {
            return;
        }
        var feedback = Feedback.Trim();
        Request.Deny(feedback.Length == 0
            ? "The user wants to keep planning. Don't make changes yet."
            : $"The user wants to keep planning and said: {feedback}");
        IsWritingFeedback = false;
        Finish(PermissionState.Denied, feedback.Length == 0 ? "Kept planning" : $"Kept planning: {feedback}");
    }

    public override bool TryAcceptFromKeyboard()
    {
        if (!IsPending || Request.DefaultToNo)
        {
            return false;
        }
        ApproveAskingForEdits();
        return true;
    }

    public override bool TryDeclineFromKeyboard()
    {
        if (!IsPending)
        {
            return false;
        }
        KeepPlanning();
        return true;
    }
}
