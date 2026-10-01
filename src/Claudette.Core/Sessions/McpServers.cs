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
    /// <summary>How Claude Code talks to it, from its <c>config.type</c>: <c>stdio</c>, <c>http</c>, <c>sse</c>, … Null when not said.</summary>
    public string? Transport { get; init; }

    /// <summary>A remote server, which can use a sign-in that <c>mcp_clear_auth</c> forgets.</summary>
    public bool IsRemote => Transport is "http" or "sse";

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
        {
            Transport = server.GetObject("config")?.GetString("type"),
        }
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

/// <summary>
/// The answer to <c>mcp_authenticate</c> (undocumented; the wire names are from Claude Code 2.1.286): the address to
/// sign in at, when the user has to, and whether Claude Code waits for the browser to come back to it on this machine.
/// </summary>
/// <param name="AuthUrl">Where to sign in; null when the server is signed in already.</param>
/// <param name="CallbackExpected">
/// Claude Code listens on this machine for the browser's return, then connects the server by itself. A claude.ai
/// connector's sign-in happens on claude.ai instead.
/// </param>
public sealed record McpSignIn(string? AuthUrl, bool RequiresUserAction, bool CallbackExpected)
{
    public static McpSignIn Parse(JsonObject response) => new(
        response.GetString("authUrl") is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? url : null,
        response.GetBool("requiresUserAction") ?? false,
        response.GetBool("callbackExpected") ?? false);
}
