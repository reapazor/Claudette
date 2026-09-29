using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claudette.Core.Protocol;

namespace Claudette.Core.RemoteControl;

/// <summary>
/// Turning Remote Control on and off for a session, and reading what Claude Code says about it (DESIGN.md §18, "Remote
/// Control"). Everything here is read tolerantly: a field or state Claudette doesn't know changes nothing.
/// </summary>
/// <remarks>
/// SDK hosts such as the VS Code extension connect with the <c>remote_control</c> control request, which Claude Code
/// 2.1.284 answers with the session's address, or with why it can't. Undocumented: the wire names come from Claude
/// Code's own handler. The documented <c>/remote-control</c> command is the fallback, for a Claude Code that rejects the
/// request; in <c>-p</c> mode 2.1.284 answers it with "isn't available in this environment" (its
/// <c>local_command_outcome</c> is <c>unavailable_headless</c>), so the fallback only says why the tab isn't connected.
/// </remarks>
public static partial class RemoteControlProtocol
{
    public const string Subtype = "remote_control";

    /// <summary>The slash command the fallback sends, as a message of its own that the tab doesn't show.</summary>
    public const string Command = "/remote-control";

    /// <summary>What <c>local_command_outcome.kind</c> says when a slash command can't run in a headless session.</summary>
    public const string UnavailableHeadless = "unavailable_headless";

    /// <summary>Connecting registers with claude.ai before Claude Code answers, over the network.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary><c>{"subtype":"remote_control","enabled":true,"name":…}</c>. The name becomes the session's title on claude.ai.</summary>
    public static JsonObject EnableRequest(string? name)
    {
        var request = new JsonObject { ["subtype"] = Subtype, ["enabled"] = true };
        if (!string.IsNullOrWhiteSpace(name))
        {
            request["name"] = name.Trim();
        }
        return request;
    }

    /// <summary><c>{"subtype":"remote_control","enabled":false}</c>: Claude Code disconnects and the local session carries on.</summary>
    public static JsonObject DisableRequest() => new() { ["subtype"] = Subtype, ["enabled"] = false };

    /// <summary>
    /// The answer to <see cref="EnableRequest"/>: connected, at <c>session_url</c> (or <c>connect_url</c>). Its
    /// <c>environment_id</c>, <c>bridge_session_id</c> and <c>bridge_epoch</c> aren't used.
    /// </summary>
    public static RemoteControlStatus FromEnabled(JsonObject response) =>
        new(RemoteControlState.Connected, ClaudeUrl(response.GetString("session_url")) ?? ClaudeUrl(response.GetString("connect_url")));

    /// <summary>
    /// Claude Code doesn't know the <c>remote_control</c> request (2.1.284 says "Unsupported control request subtype:
    /// remote_control" for one it doesn't know): time for the <c>/remote-control</c> fallback.
    /// </summary>
    public static bool IsUnsupported(string error) =>
        error.Contains("Unsupported control request", StringComparison.OrdinalIgnoreCase)
        || error.Contains("not supported in this context", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reply to the <c>/remote-control</c> fallback, read tolerantly: a session address on claude.ai means
    /// connected; words such as "isn't available", "requires" or "disabled" mean unavailable, with the reply as the
    /// reason; anything else counts as connected, with the reply as what Claude Code said.
    /// </summary>
    /// <param name="outcome">The reply's <c>local_command_outcome.kind</c>, when it has one.</param>
    public static RemoteControlStatus FromCommandReply(string reply, string? outcome = null)
    {
        var text = reply.Trim();
        var url = FindClaudeUrl(text);
        if (url is not null && IsSessionUrl(url))
        {
            return new RemoteControlStatus(RemoteControlState.Connected, url);
        }
        // Every kind but a new one means the command didn't run (unknown, failed, unavailable_headless, restart_required).
        if (outcome is UnavailableHeadless or "unknown" or "failed" or "restart_required" || text.Length == 0 || SaysUnavailable(text))
        {
            return new RemoteControlStatus(RemoteControlState.Unavailable, Detail: text.Length > 0 ? text : "Claude Code didn't say why.");
        }
        return new RemoteControlStatus(RemoteControlState.Connected, url, text);
    }

    /// <summary>The <c>state</c> of a <c>system/bridge_state</c> message. One Claudette doesn't know is <see cref="RemoteBridgeState.Unknown"/>.</summary>
    public static RemoteBridgeState BridgeState(JsonObject message) => message.GetString("state") switch
    {
        "ready" => RemoteBridgeState.Ready,
        "connected" => RemoteBridgeState.Connected,
        "reconnecting" => RemoteBridgeState.Reconnecting,
        "failed" => RemoteBridgeState.Failed,
        "policy_disabled" => RemoteBridgeState.PolicyDisabled,
        _ => RemoteBridgeState.Unknown,
    };

    /// <summary>
    /// A tab's connection after a <c>system/bridge_state</c> message, or null when it doesn't change. While Claudette
    /// waits for the answer to connecting, only a policy counts: the answer says the rest, with the address or the reason.
    /// </summary>
    /// <param name="lastUrl">The address this session last connected at, for a link Claude Code brings back by itself.</param>
    public static RemoteControlStatus? AfterBridgeState(RemoteControlStatus current, string? lastUrl, JsonObject message)
    {
        var detail = message.GetString("detail") is { Length: > 0 } said ? said.Trim() : null;
        return (BridgeState(message), current.State) switch
        {
            (RemoteBridgeState.Ready or RemoteBridgeState.Connected, RemoteControlState.Connected) when current.Detail == Reconnecting => current with { Detail = null },
            (RemoteBridgeState.Ready or RemoteBridgeState.Connected, RemoteControlState.NotConnected) when lastUrl is not null => new(RemoteControlState.Connected, lastUrl),
            (RemoteBridgeState.Reconnecting, RemoteControlState.Connected) => current with { Detail = Reconnecting },
            (RemoteBridgeState.Failed, RemoteControlState.Connected) => new(RemoteControlState.NotConnected, Detail: detail ?? "The connection to claude.ai failed."),
            (RemoteBridgeState.PolicyDisabled, RemoteControlState.Connecting or RemoteControlState.Connected) =>
                new(RemoteControlState.Unavailable, Detail: detail ?? "Remote Control is turned off by a policy."),
            _ => null,
        };
    }

    /// <summary>What a connected session's detail says while Claude Code gets its link back.</summary>
    public const string Reconnecting = "Reconnecting…";

    /// <summary>
    /// A connected tab after <c>system/worker_shutting_down</c> (documented): no longer connected, and why. Null when the
    /// tab isn't connected, so one replayed from earlier in a resumed session changes nothing.
    /// </summary>
    public static RemoteControlStatus? AfterWorkerShuttingDown(RemoteControlStatus current, JsonObject message) =>
        current.IsConnected ? new RemoteControlStatus(RemoteControlState.NotConnected, Detail: ShutdownReason(message.GetString("reason"))) : null;

    /// <summary>A <c>worker_shutting_down</c> reason in words. It's a short snake_case code such as <c>host_exit</c>.</summary>
    public static string ShutdownReason(string? reason) => reason switch
    {
        null or "" => "Claude Code closed the connection.",
        "host_exit" => "Claude Code is closing.",
        "remote_control_disabled" => "Remote Control was turned off.",
        _ => $"Claude Code closed the connection ({reason.Replace('_', ' ')}).",
    };

    /// <summary>The first claude.ai address in <paramref name="text"/>, without the punctuation after it.</summary>
    public static string? FindClaudeUrl(string text) =>
        ClaudeUrlPattern().Match(text) is { Success: true } match ? match.Value.TrimEnd('.', ',', ';', ':', '!', '?') : null;

    /// <summary>A session on claude.ai/code, rather than, say, a settings page named in an error.</summary>
    public static bool IsSessionUrl(string url) => url.Contains("claude.ai/code", StringComparison.OrdinalIgnoreCase);

    private static string? ClaudeUrl(string? url) =>
        url is { Length: > 0 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;

    private static bool SaysUnavailable(string text) => UnavailablePattern().IsMatch(text);

    [GeneratedRegex(@"https://(?:[a-z0-9-]+\.)*claude\.ai(?:/[^\s<>""'`)\]]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeUrlPattern();

    /// <summary>
    /// The words of Claude Code's "can't" replies (the Remote Control troubleshooting page): "isn't available",
    /// "requires a claude.ai subscription", "is disabled by your organization's policy", "Couldn't verify", "failed"…
    /// </summary>
    [GeneratedRegex(@"\b(isn't|is not|not|only) available\b|\bunavailable\b|\brequire[sd]?\b|\bdisabled\b|\bnot (yet )?enabled\b|\bisn't enabled\b|\bcouldn't\b|\bcould not\b|\bcan't\b|\bcannot\b|\bunable\b|\bfailed\b|\berror\b|\bexpired\b|\bnot trusted\b|\bnot enrolled\b|\bunexpected\b|\bunknown\b|\bnot logged in\b|\bnot supported\b|\bdenied\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnavailablePattern();
}
