using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.Core.Status;

/// <summary>
/// Whether a tab's session event says the Claude API is failing on Anthropic's side, so Claude's status is worth
/// checking straight away (DESIGN.md §18, "Service status"): a request retried after a server error or an overload
/// (529), by the session or one of its subagents, or a turn that ended on one. A rate limit, a sign-in problem or a
/// bad request is the user's own, and isn't.
/// </summary>
public static class ApiTrouble
{
    public static bool Reports(SessionEvent sessionEvent) => sessionEvent switch
    {
        // system/api_retry: error_status (null without an HTTP answer) and error, a category such as "overloaded".
        SystemNotice { Message.ApiRetry: { } retry } => IsServerError(retry.ErrorStatus, retry.Category),
        // tool_progress while a subagent waits out an API error: the same fields, in subagent_retry.
        ToolProgress progress when progress.Message.Raw.GetObject("subagent_retry") is { } retry => IsServerError(retry.GetDouble("error_status"), Category(retry)),
        // The turn gave up: api_error_status is the HTTP status that ended it.
        TurnCompleted { Result.IsError: true } completed => completed.Result.Raw.GetDouble("api_error_status") >= 500,
        _ => false,
    };

    private static string? Category(JsonObject raw) => raw.GetString("error_category") ?? raw.GetString("error");

    private static bool IsServerError(double? status, string? category) =>
        status is { } code ? code >= 500 : category is "overloaded" or "server_error";
}
