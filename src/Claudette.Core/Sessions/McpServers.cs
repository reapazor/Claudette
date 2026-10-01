using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>An MCP server's connection, as <c>mcp_status</c> reports it.</summary>
public enum McpServerState
{
    Unknown,
    Connected,
    Pending,
    NeedsAuth,
    Failed,
    Disabled,
}

/// <summary>
/// One MCP server of the session (DESIGN.md §4, "MCP servers"). Names and errors come from the server's configuration
/// or the server itself: untrusted text.
/// </summary>
/// <param name="Scope">Where it's configured, such as <c>project</c>, <c>user</c> or <c>local</c>, when Claude Code says.</param>
public sealed record McpServerStatus(string Name, McpServerState State, string? Error, string? Version, string? Scope, int ToolCount)
{
    /// <summary>The servers in an <c>mcp_status</c> response (<c>mcpServers</c>). Entries without a name are skipped.</summary>
    public static IReadOnlyList<McpServerStatus> ParseList(JsonObject response) =>
        (response.GetArray("mcpServers") ?? response.GetArray("mcp_servers") ?? [])
            .OfType<JsonObject>()
            .Select(Parse)
            .OfType<McpServerStatus>()
            .ToArray();

    public static McpServerStatus? Parse(JsonObject server) => server.GetString("name") is { Length: > 0 } name
        ? new McpServerStatus(
            name,
            StateOf(server.GetString("status")),
            server.GetString("error"),
            server.GetObject("serverInfo")?.GetString("version"),
            server.GetString("scope") ?? server.GetString("source"),
            server.GetArray("tools")?.Count ?? 0)
        : null;

    public static McpServerState StateOf(string? status) => status switch
    {
        "connected" => McpServerState.Connected,
        "pending" => McpServerState.Pending,
        "needs-auth" => McpServerState.NeedsAuth,
        "failed" => McpServerState.Failed,
        "disabled" => McpServerState.Disabled,
        _ => McpServerState.Unknown,
    };
}

/// <summary>What <c>rewind_files</c> did, or would do with <c>dry_run</c> (DESIGN.md §5, "Rewind and branch").</summary>
/// <param name="CanRewind">False when there's nothing to go back to, such as no checkpoint for that message.</param>
/// <param name="SkippedLinks">Paths it wouldn't touch because they're links, for safety.</param>
public sealed record RewindResult(bool CanRewind, string? Error, IReadOnlyList<string> FilesChanged, int Insertions, int Deletions, int SkippedLinks)
{
    public static RewindResult Parse(JsonObject response) => new(
        response.GetBool("canRewind") ?? response.GetBool("can_rewind") ?? false,
        response.GetString("error"),
        (response.GetArray("filesChanged") ?? response.GetArray("files_changed"))?.Select(f => f?.GetValueKind() == System.Text.Json.JsonValueKind.String ? f.GetValue<string>() : null).OfType<string>().ToArray() ?? [],
        (int)(response.GetDouble("insertions") ?? 0),
        (int)(response.GetDouble("deletions") ?? 0),
        (int)(response.GetDouble("skippedLinks") ?? response.GetDouble("skipped_links") ?? 0));
}
