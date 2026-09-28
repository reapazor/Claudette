using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>The two sign-in URLs from <c>claude_authenticate</c> (DESIGN.md §11, "Signing in").</summary>
/// <param name="AutomaticUrl">Redirects back to a local port Claude Code listens on; sign-in finishes without copying anything.</param>
/// <param name="ManualUrl">Redirects to a page that shows a code to paste into Claudette.</param>
public sealed record SignInUrls(string? AutomaticUrl, string? ManualUrl);

/// <summary>
/// A hidden <c>claude</c> process that never sends a prompt. It serves requests that don't belong to a tab: the model
/// list and account, plan usage, and sign-in (DESIGN.md §13, "Utility session").
/// </summary>
public sealed class UtilitySession : IAsyncDisposable
{
    /// <summary>Sign-in waits for the user to finish in their browser.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    private readonly ClaudeSession _session;
    private readonly Task _drain;

    private UtilitySession(ClaudeSession session)
    {
        _session = session;
        // Nobody reads this session's events; drain them so they don't pile up.
        _drain = Task.Run(async () =>
        {
            await foreach (var _ in session.Events.ReadAllAsync().ConfigureAwait(false))
            {
            }
        });
    }

    public InitializeResult Initialization => _session.Initialization!;

    public Task Completion => _session.Completion;

    public static async Task<UtilitySession> StartAsync(IClaudeSessionFactory factory, string workingDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);
        var session = await factory.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = workingDirectory,
            PersistSession = false,
            IncludePartialMessages = false,
        }, cancellationToken).ConfigureAwait(false);
        return new UtilitySession(session);
    }

    /// <summary>
    /// Plan usage limits. Undocumented: the TypeScript SDK calls this
    /// <c>usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET</c> (DESIGN.md §6, "Data source").
    /// </summary>
    public Task<JsonObject> GetUsageAsync(CancellationToken cancellationToken = default) =>
        _session.SendControlRequestAsync(new JsonObject { ["subtype"] = "get_usage" }, cancellationToken: cancellationToken);

    /// <summary>Starts sign-in and returns the URLs. Claude Code doesn't open a browser itself. Undocumented request.</summary>
    public async Task<SignInUrls> StartSignInAsync(bool claudeAiAccount = true, CancellationToken cancellationToken = default)
    {
        var response = await _session.SendControlRequestAsync(
            new JsonObject { ["subtype"] = "claude_authenticate", ["loginWithClaudeAi"] = claudeAiAccount },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SignInUrls(response.GetString("automaticUrl"), response.GetString("manualUrl"));
    }

    /// <summary>
    /// Waits until sign-in through <see cref="SignInUrls.AutomaticUrl"/> finishes. Undocumented request; its
    /// behaviour on success hasn't been observed yet (the milestone 1 spike never completed a sign-in).
    /// </summary>
    public Task<JsonObject> WaitForSignInAsync(CancellationToken cancellationToken = default) =>
        _session.SendControlRequestAsync(new JsonObject { ["subtype"] = "claude_oauth_wait_for_completion" }, SignInTimeout, cancellationToken);

    /// <summary>
    /// Completes sign-in with the code from the <see cref="SignInUrls.ManualUrl"/> page. The page shows
    /// <c>code#state</c>; both parts are sent. Undocumented request, not yet verified end to end.
    /// </summary>
    public Task<JsonObject> SubmitSignInCodeAsync(string pastedCode, CancellationToken cancellationToken = default)
    {
        var parts = pastedCode.Trim().Split('#', 2);
        var request = new JsonObject { ["subtype"] = "claude_oauth_callback", ["authorizationCode"] = parts[0] };
        if (parts.Length == 2)
        {
            request["state"] = parts[1];
        }
        return _session.SendControlRequestAsync(request, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync().ConfigureAwait(false);
        await _drain.ConfigureAwait(false);
    }
}
