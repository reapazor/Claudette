namespace Claudette.Core.Perforce;

/// <summary>Where the password for a Perforce login comes from (Settings → Perforce → Password source, DESIGN.md §18).</summary>
public enum PerforcePasswordSource
{
    /// <summary>Saved by Claudette in the OS credential store; asked for, and saved, when there isn't one yet.</summary>
    Stored,
    /// <summary>P4PASSWD from Perforce's own configuration (P4CONFIG, <c>p4 set</c>, the environment). Claudette only reads it.</summary>
    PerforceConfig,
    /// <summary>Asked for each time a login is needed, and never stored.</summary>
    AskEachTime,
}

/// <summary>One login needs a password.</summary>
/// <param name="Attempt">1 for the first try; higher after Perforce refused the previous password.</param>
/// <param name="PreviousError">Why the previous password was refused, such as "Password invalid.".</param>
public sealed record PerforcePasswordRequest(PerforceWorkspace Workspace, PerforceTarget Target, int Attempt, string? PreviousError)
{
    /// <summary>The server the login is for: the per-folder override, else the workspace's.</summary>
    public string Server => Target.Port ?? Workspace.Server;

    /// <summary>The user the login is for: the per-folder override, else the workspace's.</summary>
    public string User => Target.User ?? Workspace.User;
}

/// <summary>Supplies passwords to a <see cref="PerforceTicketKeeper"/>. The app implements it: it owns the credential store and the prompt.</summary>
public interface IPerforcePasswordProvider
{
    /// <summary>
    /// The password to log in with, or null when there's none to try: nothing is stored or configured, or the user
    /// cancelled the prompt. Called again with a higher <see cref="PerforcePasswordRequest.Attempt"/> after a refusal.
    /// </summary>
    Task<string?> GetPasswordAsync(PerforcePasswordRequest request, CancellationToken cancellationToken);

    /// <summary>The login with <paramref name="password"/> worked, for example to save a password the user just typed.</summary>
    Task OnLoginSucceededAsync(PerforcePasswordRequest request, string password, CancellationToken cancellationToken);
}
