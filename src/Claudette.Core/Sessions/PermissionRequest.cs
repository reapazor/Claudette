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
/// A <c>can_use_tool</c> request from Claude Code, waiting for the user. Call <see cref="Allow"/> or <see cref="Deny"/>
/// once; the session sends the answer.
/// </summary>
public sealed class PermissionRequest
{
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
        Raw = request;
    }

    public string RequestId { get; }

    public string ToolName { get; }

    public string? DisplayName { get; }

    public string? Description { get; }

    public string? ToolUseId { get; }

    public JsonObject Input { get; }

    /// <summary>Claude Code's suggested rules, such as <c>addRules</c> for <c>localSettings</c> or <c>setMode</c>.</summary>
    public IReadOnlyList<JsonObject> Suggestions { get; }

    public JsonObject Raw { get; }

    public bool IsCancelled { get; private set; }

    internal Task<PermissionDecision> Decision => _decision.Task;

    public void Allow(JsonObject? updatedInput = null, JsonArray? updatedPermissions = null) =>
        _decision.TrySetResult(new AllowDecision(updatedInput ?? Input, updatedPermissions));

    public void Deny(string message, bool interrupt = false) =>
        _decision.TrySetResult(new DenyDecision(message, interrupt));

    internal void Cancel()
    {
        IsCancelled = true;
        _decision.TrySetCanceled();
    }
}
