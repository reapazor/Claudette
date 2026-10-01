using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>How the user answered an MCP server's request for input.</summary>
public enum ElicitationAction
{
    /// <summary>Sent the form, or finished in the browser.</summary>
    Accept,

    /// <summary>Said no.</summary>
    Decline,

    /// <summary>Dismissed it without answering.</summary>
    Cancel,
}

/// <summary>
/// An MCP server asking the user for input through Claude Code: an <c>elicitation</c> control request (DESIGN.md §7,
/// "MCP servers asking for input"). A form asks for fields described by a JSON Schema; a URL asks the user to finish
/// something in the browser, such as signing in. Answer once with <see cref="Accept"/>, <see cref="Decline"/> or
/// <see cref="Cancel"/>; the session sends it. Undocumented on the wire (the TypeScript SDK's <c>onElicitation</c>).
/// </summary>
public sealed class ElicitationRequest
{
    private readonly TaskCompletionSource<JsonObject> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ElicitationRequest(ControlRequestMessage message)
    {
        var request = message.Request;
        RequestId = message.RequestId;
        ServerName = request.GetString("mcp_server_name") ?? request.GetString("serverName") ?? "";
        Message = request.GetString("message") ?? "";
        IsUrl = request.GetString("mode") == "url";
        Url = request.GetString("url");
        ElicitationId = request.GetString("elicitation_id") ?? request.GetString("elicitationId");
        Schema = request.GetObject("requested_schema") ?? request.GetObject("requestedSchema");
        Title = request.GetString("title");
        DisplayName = request.GetString("display_name") ?? request.GetString("displayName");
        Description = request.GetString("description");
        Raw = request;
    }

    public string RequestId { get; }

    /// <summary>The MCP server asking, by the name Claude Code knows it by. Untrusted text.</summary>
    public string ServerName { get; }

    /// <summary>What the server says it wants. Untrusted text.</summary>
    public string Message { get; }

    /// <summary>The server wants the user to visit <see cref="Url"/>, rather than fill in a form.</summary>
    public bool IsUrl { get; }

    public string? Url { get; }

    /// <summary>For a URL request: matched by the <c>system/elicitation_complete</c> message when it's done.</summary>
    public string? ElicitationId { get; }

    /// <summary>For a form: the JSON Schema of the fields, an object schema with flat properties.</summary>
    public JsonObject? Schema { get; }

    public string? Title { get; }

    public string? DisplayName { get; }

    public string? Description { get; }

    public JsonObject Raw { get; }

    /// <summary>Claude Code withdrew it, or the session ended.</summary>
    public bool IsCancelled { get; private set; }

    internal Task<JsonObject> Answer => _answer.Task;

    /// <param name="content">For a form, the values by field name.</param>
    public void Accept(JsonObject? content = null) => Respond(ElicitationAction.Accept, content);

    public void Decline() => Respond(ElicitationAction.Decline, null);

    public void Cancel() => Respond(ElicitationAction.Cancel, null);

    private void Respond(ElicitationAction action, JsonObject? content)
    {
        var response = new JsonObject
        {
            ["action"] = action switch
            {
                ElicitationAction.Accept => "accept",
                ElicitationAction.Decline => "decline",
                _ => "cancel",
            },
        };
        if (action == ElicitationAction.Accept && content is not null)
        {
            response["content"] = content.DeepClone();
        }
        _answer.TrySetResult(response);
    }

    internal void Withdraw()
    {
        IsCancelled = true;
        _answer.TrySetCanceled();
    }
}
