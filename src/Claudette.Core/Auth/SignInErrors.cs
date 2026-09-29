using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.Core.Auth;

/// <summary>Which account to sign in with (DESIGN.md §11, "More options").</summary>
public enum SignInMethod
{
    /// <summary>A Claude subscription (Pro, Max, Team or Enterprise). The default.</summary>
    ClaudeAi,

    /// <summary>An Anthropic Console account, billed for API usage.</summary>
    Console,

    /// <summary>Single sign-on. Only <c>claude auth login --sso</c> offers it; <c>claude_authenticate</c> has no option for it.</summary>
    Sso,
}

/// <summary>A sign-in that ended without signing in, with Claude Code's own message (DESIGN.md §11).</summary>
public sealed class SignInFailedException(string message) : Exception(message);

/// <summary>
/// Tells the errors a sign-in fixes from the others (DESIGN.md §11, "Detecting"). The error categories are documented;
/// the wording of a session that fails to start isn't, so that is matched loosely.
/// </summary>
public static class SignInErrors
{
    /// <summary>
    /// Phrases Claude Code uses when it has no usable sign-in, for example "Not logged in · Please run /login",
    /// "OAuth token revoked · Please run /login" or "Invalid API key · Please run /login".
    /// </summary>
    private static readonly string[] Phrases =
    [
        "/login",
        "not logged in",
        "invalid api key",
        "oauth token",
        "authentication_failed",
        "authentication_error",
        "authentication failed",
        "unauthorized",
    ];

    /// <summary>
    /// Whether an <c>assistant</c> message's <c>error</c> means signing in again helps: <c>authentication_failed</c>, or
    /// <c>oauth_org_not_allowed</c>, which a sign-in with an account from an allowed organization fixes.
    /// </summary>
    public static bool IsSignInCategory(string? assistantError) => assistantError is "authentication_failed" or "oauth_org_not_allowed";

    /// <summary>Whether a session failed to start because Claude Code isn't signed in, from the error and what it printed.</summary>
    public static bool IsSignInFailure(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var detail = ex switch
            {
                ClaudeSessionExitedException exited => exited.Exit.StandardErrorTail,
                ControlRequestException rejected => rejected.Error,
                _ => null,
            };
            if (LooksLikeSignInProblem(ex.Message) || LooksLikeSignInProblem(detail))
            {
                return true;
            }
        }
        return false;
    }

    public static bool LooksLikeSignInProblem(string? text) =>
        !string.IsNullOrEmpty(text) && Phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
}
