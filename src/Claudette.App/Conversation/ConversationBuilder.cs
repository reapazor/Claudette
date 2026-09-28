using System.Collections.ObjectModel;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.Conversation;

/// <summary>
/// Turns session events into conversation items. Must run on the UI thread, because the Markdown builders it appends
/// to are bound to the view.
/// </summary>
public sealed class ConversationBuilder(ObservableCollection<ConversationItem> items)
{
    private readonly Dictionary<string, ToolUseItem> _toolUses = [];
    private readonly Dictionary<string, PermissionItem> _permissions = [];
    private AssistantTextItem? _openText;

    // Claude Code streams a block's deltas, then sends the complete block as an assistant message. If deltas arrived
    // since the last assistant message, its text has already been shown.
    private bool _deltasSinceAssistant;

    public ObservableCollection<ConversationItem> Items { get; } = items;

    public void AddUserMessage(string text)
    {
        CloseText();
        Items.Add(new UserMessageItem(text));
    }

    public void AddNote(string text, NoteKind kind = NoteKind.Info)
    {
        CloseText();
        Items.Add(new NoteItem(text, kind));
    }

    public void Apply(SessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            // Subagent traffic (a parent tool use id) is shown in milestone 2.
            case TextDelta { ParentToolUseId: null } delta:
                _deltasSinceAssistant = true;
                AppendText(delta.Text);
                break;

            case AssistantMessageReceived { Message.ParentToolUseId: null } assistant:
                ApplyAssistant(assistant.Message);
                break;

            case ToolResultsReceived { Message.ParentToolUseId: null } results:
                foreach (var result in results.Message.Content.OfType<ToolResultBlock>())
                {
                    if (_toolUses.TryGetValue(result.ToolUseId, out var tool))
                    {
                        tool.IsComplete = true;
                        tool.IsError = result.IsError;
                        tool.ResultSummary = FirstLine(result.Text);
                    }
                }
                break;

            case LocalCommandOutputReceived local:
                AddNote(local.Text.Replace("`", "", StringComparison.Ordinal));
                break;

            case PermissionRequested permission:
                CloseText();
                var item = new PermissionItem(permission.Request);
                _permissions[permission.Request.RequestId] = item;
                Items.Add(item);
                break;

            case PermissionCancelled cancelled:
                if (_permissions.TryGetValue(cancelled.RequestId, out var cancelledItem) && cancelledItem.IsPending)
                {
                    cancelledItem.State = PermissionState.Cancelled;
                }
                break;

            case TurnCompleted completed:
                CloseText();
                if (completed.Result.IsError && completed.Result.TerminalReason != "aborted_streaming" && completed.Result.Result is { Length: > 0 } error)
                {
                    AddNote(error, NoteKind.Error);
                }
                else if (completed.Result.TerminalReason == "aborted_streaming")
                {
                    AddNote("Stopped.", NoteKind.Warning);
                }
                break;

            case ProtocolError protocolError:
                AddNote($"Skipped a message Claudette couldn't read: {protocolError.Error}", NoteKind.Warning);
                break;

            case SessionExited exited:
                CloseText();
                foreach (var pending in _permissions.Values.Where(p => p.IsPending))
                {
                    pending.State = PermissionState.Cancelled;
                }
                var code = exited.Exit.ExitCode?.ToString() ?? "unknown";
                var detail = string.IsNullOrWhiteSpace(exited.Exit.StandardErrorTail) ? "" : $"\n{LastLines(exited.Exit.StandardErrorTail, 5)}";
                AddNote($"Claude Code exited (code {code}).{detail}", exited.Exit.ExitCode == 0 ? NoteKind.Info : NoteKind.Error);
                break;
        }
    }

    private void ApplyAssistant(AssistantMessage message)
    {
        var streamed = _deltasSinceAssistant;
        _deltasSinceAssistant = false;
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    if (!streamed)
                    {
                        AppendText(text.Text);
                    }
                    CloseText();
                    break;

                case ToolUseBlock toolUse:
                    CloseText();
                    var tool = new ToolUseItem(toolUse.Id, toolUse.Name, ToolUseItem.Summarize(toolUse.Input));
                    _toolUses[toolUse.Id] = tool;
                    Items.Add(tool);
                    break;
            }
        }
    }

    private void AppendText(string text)
    {
        if (_openText is null)
        {
            _openText = new AssistantTextItem();
            Items.Add(_openText);
        }
        _openText.Append(text);
    }

    private void CloseText()
    {
        if (_openText is not null)
        {
            _openText.IsStreaming = false;
            _openText = null;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');
        var first = (end >= 0 ? line[..end] : line).ToString();
        return first.Length > 160 ? first[..157] + "…" : first;
    }

    private static string LastLines(string text, int count) =>
        string.Join('\n', text.Split('\n').TakeLast(count));
}
