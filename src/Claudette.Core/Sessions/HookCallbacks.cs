using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>
/// What Claude Code passes to a hook callback: the hook's input JSON (DESIGN.md §13, "Hook callbacks"), for example
/// <c>hook_event_name</c>, <c>tool_name</c>, <c>tool_input</c> and <c>tool_use_id</c> for PreToolUse.
/// </summary>
public sealed record HookInput(string? EventName, string? ToolName, JsonObject? ToolInput, string? ToolUseId, JsonObject Raw)
{
    /// <summary>The command, for a Bash tool call.</summary>
    public string? Command => ToolInput?.GetString("command");

    /// <summary>Reads a <c>hook_callback</c> request. Unknown fields are ignored.</summary>
    public static HookInput Parse(JsonObject request)
    {
        var input = request.GetObject("input") ?? [];
        return new HookInput(
            input.GetString("hook_event_name"),
            input.GetString("tool_name"),
            input.GetObject("tool_input"),
            input.GetString("tool_use_id") ?? request.GetString("tool_use_id"),
            input);
    }
}

/// <summary>
/// Runs when Claude Code calls the hook back. Returns the hook's output JSON, such as <see cref="HookOutputs.Continue"/>.
/// The token is cancelled when Claude Code withdraws the call (<c>control_cancel_request</c>: the turn was interrupted,
/// or the hook timed out) or the session ends; nothing is answered then.
/// </summary>
public delegate Task<JsonObject> HookCallback(HookInput input, CancellationToken cancellationToken);

/// <summary>
/// A hook Claudette registers through the <c>hooks</c> field of the <c>initialize</c> request, the way the Agent SDKs
/// register hook callbacks.
/// </summary>
/// <param name="Event">The hook event, such as <c>PreToolUse</c>.</param>
/// <param name="Matcher">Which tools it applies to, such as <c>Bash</c>; null for all.</param>
/// <param name="Timeout">How long Claude Code waits for the answer (its default is 60 seconds). A PreToolUse call that times out doesn't run the tool.</param>
public sealed record HookRegistration(string Event, string? Matcher, HookCallback Callback, TimeSpan? Timeout = null);

public static class HookOutputs
{
    /// <summary>Lets the tool call go ahead as it is: no decision, no changed input.</summary>
    public static JsonObject Continue() => new() { ["continue"] = true };

    /// <summary>
    /// Stops a PreToolUse call: the tool doesn't run, and Claude gets <paramref name="reason"/> as the tool's result, an
    /// error (checked against 2.1.284).
    /// </summary>
    public static JsonObject Deny(string reason) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason,
        },
    };
}

/// <summary>The registered hooks: the <c>hooks</c> field of <c>initialize</c>, and the callbacks by id.</summary>
internal sealed class HookCallbackRegistry
{
    private readonly Dictionary<string, HookCallback> _callbacks = new(StringComparer.Ordinal);

    public HookCallbackRegistry(IReadOnlyList<HookRegistration> hooks)
    {
        if (hooks.Count == 0)
        {
            return;
        }
        var config = new JsonObject();
        foreach (var hook in hooks)
        {
            // hook_0, hook_1, … as the Python Agent SDK numbers them.
            var id = $"hook_{_callbacks.Count}";
            _callbacks[id] = hook.Callback;
            if (config[hook.Event] is not JsonArray matchers)
            {
                matchers = [];
                config[hook.Event] = matchers;
            }
            var matcher = new JsonObject { ["matcher"] = hook.Matcher, ["hookCallbackIds"] = new JsonArray(id) };
            if (hook.Timeout is { } timeout)
            {
                matcher["timeout"] = (int)Math.Ceiling(timeout.TotalSeconds);
            }
            matchers.Add(matcher);
        }
        Config = config;
    }

    /// <summary>The <c>hooks</c> field of <c>initialize</c>, or null when there are none.</summary>
    public JsonObject? Config { get; }

    public HookCallback? Find(string callbackId) => _callbacks.GetValueOrDefault(callbackId);
}
