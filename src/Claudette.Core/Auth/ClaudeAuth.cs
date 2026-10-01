using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Claude;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Claudette.Core.Json;

namespace Claudette.Core.Auth;

/// <summary>The output of <c>claude auth status</c> (DESIGN.md §11, "Detecting").</summary>
public sealed record AuthStatus(
    bool LoggedIn,
    string? AuthMethod,
    string? ApiProvider,
    string? Email,
    string? OrganizationName,
    string? SubscriptionType,
    string? ConfigDirectory,
    string? ProjectsDirectory);

/// <summary>Runs Claude Code's documented <c>auth</c> commands.</summary>
/// <param name="environment">Added to the clean environment of each command. Tests use it for fake-claude's options.</param>
/// <param name="userEnvironment">The user environment the commands start from (DESIGN.md §13). Null: Claudette's own.</param>
public sealed class ClaudeAuth(
    string claudePath,
    IProcessLauncher launcher,
    TimeProvider timeProvider,
    IReadOnlyDictionary<string, string?>? environment = null,
    UserEnvironment? userEnvironment = null)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long <c>claude auth login</c> waits for the user to finish in the browser.</summary>
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    public async Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["auth", "status"], cancellationToken).ConfigureAwait(false);
        // Exits 1 when signed out, but still prints the JSON.
        return TryParseStatus(result.StandardOutput, out var status)
            ? status
            : throw new InvalidOperationException($"Couldn't read 'claude auth status' (exit {result.ExitCode}): {result.StandardError.Trim()}");
    }

    /// <summary>
    /// Runs <c>claude auth logout</c>, which signs Claude Code out everywhere on this computer (DESIGN.md §11,
    /// "Account menu"). Throws with the command's message when it fails.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["auth", "logout"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var message = ClaudeLogin.StripEscapes(result.StandardError).Trim();
            throw new InvalidOperationException(message.Length > 0 ? message : $"'claude auth logout' ended with exit code {result.ExitCode}.");
        }
    }

    /// <summary>
    /// Starts <c>claude auth login</c> (DESIGN.md §11): the fallback when the sign-in control requests aren't
    /// available, and the way to sign in with SSO.
    /// </summary>
    public ClaudeLogin StartLogin(SignInMethod method) =>
        ClaudeLogin.Start(launcher, Spec(ClaudeLogin.Arguments(method), userEnvironment?.Current), LoginTimeout, timeProvider);

    public static bool TryParseStatus(string json, out AuthStatus status)
    {
        status = new AuthStatus(false, null, null, null, null, null, null, null);
        JsonObject? obj;
        try
        {
            obj = JsonTree.ParseObject(json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (obj?.GetBool("loggedIn") is not { } loggedIn)
        {
            return false;
        }
        status = new AuthStatus(
            loggedIn,
            obj.GetString("authMethod"),
            obj.GetString("apiProvider"),
            obj.GetString("email"),
            obj.GetString("orgName"),
            obj.GetString("subscriptionType"),
            obj.GetString("configDirectory"),
            obj.GetString("projectsDirectory"));
        return true;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var baseEnvironment = userEnvironment is null ? null : await userEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        return await ProcessRunner.RunAsync(launcher, Spec(args, baseEnvironment), CommandTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private ProcessStartSpec Spec(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? baseEnvironment) =>
        new(claudePath, args) { Environment = ClaudeEnvironment.From(baseEnvironment, environment) };
}
