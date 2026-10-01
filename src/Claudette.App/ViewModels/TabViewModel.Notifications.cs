using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.ViewModels;

/// <summary>This tab's OS notifications (DESIGN.md §10). The notification service decides whether each one is sent.</summary>
public sealed partial class TabViewModel
{
    partial void OnStatusChanged(TabStatus value) => _shell.OnTabStatusChanged();

    /// <summary>A turn ended while the user may not be looking.</summary>
    private void NotifyTurnFinished(ResultMessage result)
    {
        var reply = result.Result?.Trim();
        var body = string.IsNullOrEmpty(reply) ? "Claude finished its turn." : Shorten(reply.Split('\n')[0].Trim(), 140);
        _services.Notifications.Notify(NotificationKind.TurnFinished, DisplayName, body, Id);
        _shell.Announce($"{DisplayName}: Claude finished.");
    }

    /// <summary>A permission prompt, question or plan is waiting (DESIGN.md §7).</summary>
    private void NotifyNeedsInput(PermissionRequest request)
    {
        var body = Items.OfType<PromptItem>().LastOrDefault(p => ReferenceEquals(p.Request, request)) switch
        {
            PermissionItem permission => (permission.Command ?? permission.Detail) is { } what
                ? $"{permission.Title} {Shorten(what, 120)}"
                : permission.Title,
            QuestionItem => (request.Input["questions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.GetString("question") is { } question
                ? $"Claude has a question: {Shorten(question, 120)}"
                : "Claude has a question.",
            PlanItem => "Claude has a plan for you to review.",
            _ => "Claude is waiting for you.",
        };
        _services.Notifications.Notify(NotificationKind.NeedsInput, DisplayName, body, Id);
        _shell.Announce($"{DisplayName}: {body}");
    }

    /// <summary>Claude Code stopped with an error, or couldn't start.</summary>
    private void NotifyProcessError(string body)
    {
        _services.Notifications.Notify(NotificationKind.ProcessError, DisplayName, body, Id);
        _shell.Announce($"{DisplayName}: {body}");
    }

    /// <summary>"Notify me when a check-in is sent" (DESIGN.md §5).</summary>
    private void NotifyCheckIn() =>
        _services.Notifications.Notify(NotificationKind.CheckIn, DisplayName,
            "Claude has been at this a while, so Claudette asked how it's going.", Id, checkIns: CheckInSettings);
}
