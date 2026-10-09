using System.Text.Json.Nodes;

namespace Claudette.Core.Protocol;

/// <summary>
/// A user message's <c>origin</c>: who it's from (DESIGN.md §13, "Who a message is from"). Claudette writes
/// <c>{kind: "human"}</c> on what the user typed. Claude Code echoes a message another of the user's sessions sent this
/// one (cross-session messaging) with <c>{kind: "peer", name, body, from, msg_id, fromMode}</c> (checked against 2.1.284;
/// DESIGN.md §18, "Threads").
/// </summary>
/// <param name="Kind">Such as <c>human</c>, <c>peer</c> or <c>task-notification</c>.</param>
/// <param name="Name">For a peer, the sending session's name.</param>
/// <param name="Body">For a peer, the message as it wrote it, without the wrapping the model reads.</param>
public sealed record MessageOrigin(string? Kind, string? Name, string? Body)
{
    /// <summary>Another session sent it.</summary>
    public bool IsPeer => Kind == "peer";

    public static MessageOrigin? Read(JsonObject? origin) =>
        origin is null ? null : new MessageOrigin(origin.GetString("kind"), origin.GetString("name"), origin.GetString("body"));
}
