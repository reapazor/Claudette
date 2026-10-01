using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>
/// Per-call token usage from <c>assistant</c> messages (DESIGN.md §6, "Data source"): the tokens of the turn in progress,
/// before its <c>result</c> gives the totals, and how full the context was after the main agent's latest call. Claude Code
/// sends one <c>assistant</c> message per content block and repeats the call's usage on each, so calls are counted once
/// by message id, keeping the latest figures.
/// </summary>
public sealed class CallUsage
{
    private readonly Dictionary<string, long> _turnCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _contextWindows = new(StringComparer.Ordinal);

    /// <summary>Tokens of the calls in the turn so far, including subagents' calls.</summary>
    public long TurnTokens => _turnCalls.Values.Sum();

    /// <summary>Tokens in the main agent's context after its latest call: what it read, plus what it wrote.</summary>
    public long? ContextTokens { get; private set; }

    /// <summary>The model of the main agent's latest call.</summary>
    public string? Model { get; private set; }

    /// <summary>Counts an assistant message's call. Returns false when it has no usage or repeats one already counted.</summary>
    public bool Add(AssistantMessage message)
    {
        if (message.Raw.GetObject("message")?.GetObject("usage") is not { } usage)
        {
            return false;
        }
        var input = Number(usage["input_tokens"]) + Number(usage["cache_creation_input_tokens"]) + Number(usage["cache_read_input_tokens"]);
        var total = input + Number(usage["output_tokens"]);
        var key = message.MessageId ?? $"call-{_turnCalls.Count}";
        var changed = !_turnCalls.TryGetValue(key, out var previous) || previous != total;
        _turnCalls[key] = total;
        if (message.ParentToolUseId is null)
        {
            changed |= ContextTokens != total;
            ContextTokens = total;
            Model = message.Model ?? Model;
        }
        return changed;
    }

    /// <summary>The turn ended: its result carries the totals, and each model's context window.</summary>
    public void TurnEnded(ResultMessage result)
    {
        _turnCalls.Clear();
        if (result.ModelUsage is null)
        {
            return;
        }
        foreach (var (model, node) in result.ModelUsage)
        {
            if (node is JsonObject usage && Number(usage["contextWindow"]) is > 0 and var window)
            {
                _contextWindows[model] = window;
            }
        }
    }

    /// <summary>The conversation was cleared or compacted: the last call no longer says how full the context is.</summary>
    public void ContextReset() => ContextTokens = null;

    /// <summary>
    /// The context window of the latest call's model, as the last <c>result</c> reported it. Null before a turn has ended
    /// with that model.
    /// </summary>
    public long? ContextWindow => Model is { } model && _contextWindows.TryGetValue(model, out var window) ? window : null;

    /// <summary>
    /// How full the context is, when <c>get_context_usage</c> isn't available: the latest call's tokens ÷ its model's
    /// context window (DESIGN.md §6, "Per-tab context").
    /// </summary>
    public double? ContextPercentage => ContextTokens is { } tokens && ContextWindow is { } window ? Math.Min(100, 100.0 * tokens / window) : null;

    private static long Number(JsonNode? node) => node.AsWholeNumber() ?? 0;
}
