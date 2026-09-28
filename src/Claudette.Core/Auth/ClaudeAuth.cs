using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Claude;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;

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
public sealed class ClaudeAuth(string claudePath, IProcessLauncher launcher, TimeProvider timeProvider)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["auth", "status"], cancellationToken).ConfigureAwait(false);
        // Exits 1 when signed out, but still prints the JSON.
        return TryParseStatus(result.StandardOutput, out var status)
            ? status
            : throw new InvalidOperationException($"Couldn't read 'claude auth status' (exit {result.ExitCode}): {result.StandardError.Trim()}");
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["auth", "logout"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'claude auth logout' failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    public static bool TryParseStatus(string json, out AuthStatus status)
    {
        status = new AuthStatus(false, null, null, null, null, null, null, null);
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
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

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(
            launcher,
            new ProcessStartSpec(claudePath, args) { Environment = ClaudeEnvironment.Create() },
            CommandTimeout,
            timeProvider,
            cancellationToken);
}
