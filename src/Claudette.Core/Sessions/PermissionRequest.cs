using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>How the user answered a permission prompt.</summary>
public abstract record PermissionDecision
{
    internal abstract JsonObject ToResponse();
}

/// <param name="UpdatedPermissions">Rules to save, such as an "Always allow" rule (DESIGN.md §7).</param>
public sealed record AllowDecision(JsonObject UpdatedInput, JsonArray? UpdatedPermissions) : PermissionDecision
{
    internal override JsonObject ToResponse()
    {
        var response = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = UpdatedInput.DeepClone() };
        if (UpdatedPermissions is not null)
        {
            response["updatedPermissions"] = UpdatedPermissions.DeepClone();
        }
        return response;
    }
}

public sealed record DenyDecision(string Message, bool Interrupt) : PermissionDecision
{
    internal override JsonObject ToResponse()
    {
        var response = new JsonObject { ["behavior"] = "deny", ["message"] = Message };
        if (Interrupt)
        {
            response["interrupt"] = true;
        }
        return response;
    }
}

/// <summary>
/// A <c>can_use_tool</c> request from Claude Code, waiting for the user. Call one of the Allow methods or
/// <see cref="Deny"/> once; the session sends the answer.
/// </summary>
public sealed class PermissionRequest
{
    /// <summary>Claude Code's personal, uncommitted project settings file (<c>.claude/settings.local.json</c>).</summary>
    public const string LocalSettings = "localSettings";

    /// <summary>Applies to this session only; nothing is saved.</summary>
    public const string Session = "session";

    private readonly TaskCompletionSource<PermissionDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PermissionRequest(ControlRequestMessage message)
    {
        var request = message.Request;
        RequestId = message.RequestId;
        ToolName = request.GetString("tool_name") ?? "";
        DisplayName = request.GetString("display_name");
        Description = request.GetString("description");
        ToolUseId = request.GetString("tool_use_id");
        Input = request.GetObject("input") ?? [];
        Suggestions = request.GetArray("permission_suggestions")?.OfType<JsonObject>().ToArray() ?? [];
        // Only the TypeScript option names are documented; accept the snake_case wire spelling too.
        DecisionReason = request.GetString("decision_reason") ?? request.GetString("decisionReason");
        BlockedPath = request.GetString("blocked_path") ?? request.GetString("blockedPath");
        AgentId = request.GetString("agent_id") ?? request.GetString("agentID");
        DefaultToNo = (request.GetBool("default_to_no") ?? request.GetBool("defaultToNo")) == true;
        SuppressAlwaysAllowRule = (request.GetBool("suppress_always_allow_rule") ?? request.GetBool("suppressAlwaysAllowRule")) == true;
        Raw = request;
    }

    public string RequestId { get; }

    public string ToolName { get; }

    public string? DisplayName { get; }

    public string? Description { get; }

    public string? ToolUseId { get; }

    public JsonObject Input { get; }

    /// <summary>Claude Code's suggested updates, such as <c>addRules</c> for <c>localSettings</c> or <c>setMode</c>.</summary>
    public IReadOnlyList<JsonObject> Suggestions { get; }

    /// <summary>Why Claude Code is asking, when it says.</summary>
    public string? DecisionReason { get; }

    /// <summary>The file path that triggered the request, if any.</summary>
    public string? BlockedPath { get; }

    /// <summary>Set when the request comes from inside a subagent.</summary>
    public string? AgentId { get; }

    /// <summary>A stray keystroke mustn't approve this request: no one-key approve shortcut.</summary>
    public bool DefaultToNo { get; }

    /// <summary>Don't offer "Always allow": the rule would grant more than this request.</summary>
    public bool SuppressAlwaysAllowRule { get; }

    public JsonObject Raw { get; }

    public bool IsCancelled { get; private set; }

    /// <summary>The allow rules Claude Code suggests saving, from its <c>addRules</c> suggestions (DESIGN.md §7).</summary>
    public IReadOnlyList<PermissionRule> SuggestedRules => Suggestions
        .Where(s => s.GetString("type") == "addRules" && (s.GetString("behavior") ?? "allow") == "allow")
        .SelectMany(s => s.GetArray("rules")?.OfType<JsonObject>() ?? [])
        .Select(r => r.GetString("toolName") is { Length: > 0 } tool ? new PermissionRule(tool, r.GetString("ruleContent")) : null)
        .OfType<PermissionRule>()
        .Distinct()
        .ToArray();

    /// <summary>A permission mode Claude Code suggests switching to, such as <c>acceptEdits</c> for a file edit.</summary>
    public string? SuggestedMode => Suggestions.FirstOrDefault(s => s.GetString("type") == "setMode")?.GetString("mode");

    internal Task<PermissionDecision> Decision => _decision.Task;

    public void Allow(JsonObject? updatedInput = null, JsonArray? updatedPermissions = null) =>
        _decision.TrySetResult(new AllowDecision(updatedInput ?? Input, updatedPermissions));

    /// <summary>
    /// Allows and saves <paramref name="rules"/>. Claude Code writes the file itself: <see cref="LocalSettings"/> goes
    /// to <c>.claude/settings.local.json</c>, <see cref="Session"/> saves nothing. Any directory suggestions go along
    /// with the destination Claude Code suggested for them, or for this session only when nothing is to be saved.
    /// </summary>
    public void AllowAlways(IReadOnlyList<PermissionRule> rules, string destination)
    {
        var updates = new JsonArray();
        if (rules.Count > 0)
        {
            updates.Add(PermissionUpdates.AddRules(rules, destination));
        }
        foreach (var directories in Suggestions.Where(s => s.GetString("type") == "addDirectories"))
        {
            var update = (JsonObject)directories.DeepClone();
            if (destination == Session)
            {
                update["destination"] = Session;
            }
            updates.Add(update);
        }
        Allow(updatedPermissions: updates);
    }

    /// <summary>Allows, and switches the session's permission mode (for example to accept edits from now on).</summary>
    public void AllowAndSetMode(string mode) =>
        Allow(updatedPermissions: [PermissionUpdates.SetMode(mode, Session)]);

    public void Deny(string message, bool interrupt = false) =>
        _decision.TrySetResult(new DenyDecision(message, interrupt));

    internal void Cancel()
    {
        IsCancelled = true;
        _decision.TrySetCanceled();
    }
}
