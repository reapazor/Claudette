using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>How full the session's context window is, from <c>get_context_usage</c> (DESIGN.md §6, "Per-tab context").</summary>
public sealed record ContextUsage(
    long TotalTokens,
    long MaxTokens,
    double Percentage,
    long? AutoCompactThreshold,
    bool AutoCompactEnabled,
    JsonObject Raw)
{
    public static ContextUsage Parse(JsonObject response)
    {
        var total = (long)(response.GetDouble("totalTokens") ?? 0);
        var max = (long)(response.GetDouble("maxTokens") ?? 0);
        var percentage = response.GetDouble("percentage") ?? (max > 0 ? 100.0 * total / max : 0);
        var threshold = response.GetDouble("autoCompactThreshold") is { } t ? (long)t : (long?)null;
        return new ContextUsage(total, max, percentage, threshold, response.GetBool("isAutoCompactEnabled") ?? false, response);
    }
}
