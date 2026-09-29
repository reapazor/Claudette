using Claudette.Core.Auth;

namespace Claudette.Core.RemoteControl;

/// <summary>
/// Whether the signed-in account can use Remote Control, from what Claudette already knows: <c>claude auth status</c>
/// and the environment it gives <c>claude</c> (DESIGN.md §18, "Remote Control"). Remote Control needs a claude.ai
/// subscription sign-in talking to <c>api.anthropic.com</c>. Anything else Claude Code checks itself when a tab connects
/// (a policy, feature flags, the plan), and the tab shows its reason.
/// </summary>
public static class RemoteControlEligibility
{
    public const string BaseUrlVariable = "ANTHROPIC_BASE_URL";

    /// <summary>Why the account can't use Remote Control, or null when it may, or Claudette can't tell yet.</summary>
    /// <param name="account">The latest <c>claude auth status</c>; null before the first.</param>
    /// <param name="environment">A variable of the environment <c>claude</c> starts with, or null when it isn't set.</param>
    public static string? Check(AuthStatus? account, Func<string, string?> environment)
    {
        if (account is { LoggedIn: false })
        {
            return "Claude Code isn't signed in. Remote Control needs a claude.ai subscription sign-in.";
        }
        if (account?.AuthMethod is "api_key" or "api_key_helper")
        {
            return "Claude Code is signed in with an API key. Remote Control needs a claude.ai subscription sign-in.";
        }
        if (account?.AuthMethod == "third_party" || account?.ApiProvider is { Length: > 0 } provider && provider != "firstParty")
        {
            return "Claude Code is using a cloud provider (Amazon Bedrock, Google Cloud or Microsoft Foundry). Remote Control only works with a claude.ai subscription.";
        }
        if (environment(BaseUrlVariable) is { Length: > 0 } baseUrl && !IsAnthropicApi(baseUrl))
        {
            return $"{BaseUrlVariable} points Claude Code at {Host(baseUrl)}. Remote Control only works through api.anthropic.com.";
        }
        return null;
    }

    private static bool IsAnthropicApi(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && string.Equals(uri.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase);

    private static string Host(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : "another address";
}
