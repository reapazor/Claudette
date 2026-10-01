using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>What the tab can offer for a startup failure, besides saying why (DESIGN.md §4, "Why it couldn't start").</summary>
public enum StartupFailureFix
{
    /// <summary>Nothing Claudette can do here: the explanation says what to change.</summary>
    None,

    /// <summary>The folder is gone or unreadable: choose where it is now, as for a missing folder.</summary>
    ChooseFolder,
}

/// <summary>
/// Why Claude Code refused to start: the <c>startup_failure_reason</c> on the <c>error_during_execution</c> result it
/// writes before exiting, with <c>CLAUDE_CODE_STARTUP_FAILURE_RESULTS=1</c> (documented), and its <c>errors</c>, the same
/// text as its error output. A reason Claudette doesn't know is shown by its errors alone.
/// </summary>
public sealed record StartupFailure(string Reason, string Errors)
{
    /// <summary>The variable that makes Claude Code write the result for every startup failure, not just a few.</summary>
    public const string ResultsVariable = "CLAUDE_CODE_STARTUP_FAILURE_RESULTS";

    /// <summary>The failure a result reports, or null for any other result.</summary>
    public static StartupFailure? From(ResultMessage result) =>
        result.Raw.GetString("startup_failure_reason") is { Length: > 0 } reason
            ? new StartupFailure(reason, string.Join('\n', result.Raw.GetStringList("errors")).Trim())
            : null;

    /// <summary>Why it couldn't start, in a sentence or two, with what to do; null for a reason Claudette doesn't know.</summary>
    public string? Explanation => Reason switch
    {
        "org_pin_api_key_conflict" => "Your organization's settings require signing in with its account, but an API key, auth token or apiKeyHelper is set. Remove it, then restart the tab.",
        "provider_not_allowed" => "Your organization's settings don't allow the API provider or endpoint this session is set up for.",
        "org_verify_failed" => "Claude Code couldn't check your sign-in's organization against your organization's settings, for example because of a network failure or a revoked sign-in.",
        "org_pin_mismatch" => "You're signed in to an organization your organization's settings don't allow. Sign in with another account.",
        "managed_settings_invalid" => "Your organization's managed settings couldn't be read, or they leave no model to use.",
        "remote_settings_required_unavailable" => "Settings your organization requires couldn't be loaded. Check the network, then restart the tab.",
        "gateway_signin_required" => "The Claude apps gateway ended this sign-in. Sign in again.",
        "gateway_access_denied" => "The Claude apps gateway refused access to your organization's settings.",
        "proxy_invalid" => "A proxy setting isn't a complete URL. Fix or unset it, then restart the tab.",
        "temp_dir_unusable" => "Claude Code's temporary folder is unsafe or couldn't be created.",
        "cwd_unavailable" => "The tab's folder was deleted or moved, or can't be read.",
        "shell_tool_missing" => "Claude Code needs Git for Windows (Git Bash) to run commands, and it isn't installed. Install it from https://git-scm.com/downloads/win, then restart the tab.",
        "session_held_by_background" => "This conversation is running as a background session in Claude Code. Stop it there, or open a copy from History.",
        "worktree_resume_refused" => "Claude Code wouldn't resume this session in its worktree.",
        "worktree_unverified" => "Claude Code couldn't check this session's worktree just now. Restarting the tab may work.",
        "cli_version_too_old" => "This Claude Code is older than Anthropic allows. Update it (Settings → Claude Code), then restart the tab.",
        "bypass_root" => "Bypass permissions mode can't be used while running as root. Choose another permission mode in Tab settings…, then restart the tab.",
        _ => null,
    };

    /// <summary>The line the tab's row and info card show.</summary>
    public string Summary => Explanation ?? (Errors.Length > 0 ? Errors.Split('\n')[0] : $"Claude Code refused to start ({Reason.Replace('_', ' ')}).");

    /// <summary>The note in the conversation: why, then what Claude Code said, when that adds something.</summary>
    public string Message => Explanation is { } explanation && Errors.Length > 0 ? $"{explanation}\nClaude Code said: {Errors}" : Explanation ?? (Errors.Length > 0 ? Errors : Summary);

    public StartupFailureFix Fix => Reason == "cwd_unavailable" ? StartupFailureFix.ChooseFolder : StartupFailureFix.None;
}
