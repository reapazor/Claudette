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
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private TaskCompletionSource<ResultMessage>? _pendingCommand;

    private UtilitySession(ClaudeSession session)
    {
        _session = session;
        // Nobody reads this session's events except to finish a local command; drain them so they don't pile up.
        _drain = Task.Run(async () =>
        {
            await foreach (var sessionEvent in session.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (sessionEvent is TurnCompleted completed)
                {
                    Volatile.Read(ref _pendingCommand)?.TrySetResult(completed.Result);
                }
            }
            Volatile.Read(ref _pendingCommand)?.TrySetCanceled();
        });
    }

    public InitializeResult Initialization => _session.Initialization!;

    public Task Completion => _session.Completion;

    /// <param name="protocolLogPath">Where to log its protocol traffic, or null (DESIGN.md §13, "Logging").</param>
    public static async Task<UtilitySession> StartAsync(IClaudeSessionFactory factory, string workingDirectory, CancellationToken cancellationToken = default, string? protocolLogPath = null)
    {
        Directory.CreateDirectory(workingDirectory);
        var session = await factory.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = workingDirectory,
            PersistSession = false,
            IncludePartialMessages = false,
            ProtocolLogPath = protocolLogPath,
        }, cancellationToken).ConfigureAwait(false);
        return new UtilitySession(session);
    }

    /// <summary>
    /// Plan usage limits. Undocumented: the TypeScript SDK calls this
    /// <c>usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET</c> (DESIGN.md §6, "Data source").
    /// </summary>
    public Task<JsonObject> GetUsageAsync(CancellationToken cancellationToken = default) =>
        _session.SendControlRequestAsync(new JsonObject { ["subtype"] = "get_usage" }, cancellationToken: cancellationToken);

    /// <summary>
    /// Runs a local slash command such as <c>/usage</c> and returns the text it prints. Local commands make no model
    /// call, so this is free (DESIGN.md §6, "Data source", source 3). One at a time.
    /// </summary>
    public async Task<string?> RunLocalCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new TaskCompletionSource<ResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pendingCommand, pending);
            await _session.SendUserMessageAsync(command, cancellationToken).ConfigureAwait(false);
            var result = await pending.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return result.IsError ? null : result.Result;
        }
        finally
        {
            Volatile.Write(ref _pendingCommand, null);
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Starts sign-in and returns the URLs. Claude Code doesn't open a browser itself. Undocumented request.
    /// </summary>
    /// <param name="claudeAiAccount">
    /// <c>loginWithClaudeAi</c>: true for a Claude subscription, false for an Anthropic Console account (DESIGN.md §11,
    /// "More options"). Claude Code refuses a choice that managed settings' <c>forceLoginMethod</c> rules out.
    /// </param>
    public async Task<SignInUrls> StartSignInAsync(bool claudeAiAccount = true, CancellationToken cancellationToken = default)
    {
        var response = await _session.SendControlRequestAsync(
            new JsonObject { ["subtype"] = "claude_authenticate", ["loginWithClaudeAi"] = claudeAiAccount },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SignInUrls(response.GetString("automaticUrl"), response.GetString("manualUrl"));
    }

    /// <summary>
    /// Waits until the sign-in started by <see cref="StartSignInAsync"/> finishes, by either URL. Undocumented request.
    /// In Claude Code 2.1.284's source it answers with the new <c>account</c> once signed in, and with the sign-in's
    /// error (for example an organization that isn't allowed) if it fails; no real sign-in has completed through it yet.
    /// </summary>
    public Task<JsonObject> WaitForSignInAsync(CancellationToken cancellationToken = default) =>
        _session.SendControlRequestAsync(new JsonObject { ["subtype"] = "claude_oauth_wait_for_completion" }, SignInTimeout, cancellationToken);

    /// <summary>
    /// Completes sign-in with the code from the <see cref="SignInUrls.ManualUrl"/> page. The page shows
    /// <c>code#state</c>; both parts are sent, as <c>authorizationCode</c> and <c>state</c> like the TypeScript SDK's
    /// <c>claudeOAuthCallback</c>. Answers once the sign-in has finished, like
    /// <see cref="WaitForSignInAsync"/>. Undocumented request, not yet verified end to end.
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
        _commandLock.Dispose();
    }
}
