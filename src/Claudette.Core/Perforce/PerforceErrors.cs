namespace Claudette.Core.Perforce;

/// <summary>Perforce's error messages that ticket handling reacts to (DESIGN.md §18).</summary>
public static class PerforceErrors
{
    /// <summary>"Your session has expired, please login again." (or "was logged out").</summary>
    public static bool IsSessionExpired(string text) =>
        text.Contains("Your session has expired", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Your session was logged out", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Perforce password (P4PASSWD) invalid or unset."</summary>
    public static bool IsPasswordUnset(string text) =>
        text.Contains("P4PASSWD) invalid or unset", StringComparison.OrdinalIgnoreCase);

    /// <summary>A <c>p4</c> command failed because it needs a login: what recovery looks for in a Bash result.</summary>
    public static bool NeedsLogin(string text) => IsSessionExpired(text) || IsPasswordUnset(text);

    /// <summary>"Connect to server failed; check $P4PORT." and the TCP errors that come with it.</summary>
    public static bool IsConnectionFailure(string text) =>
        text.Contains("Connect to server failed", StringComparison.OrdinalIgnoreCase)
        || text.Contains("check $P4PORT", StringComparison.OrdinalIgnoreCase)
        || text.Contains("TCP connect to", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Output that means the login needs the user: single sign-on or a second factor, which Claudette doesn't
    /// handle (DESIGN.md §18, "Not covered").
    /// </summary>
    public static bool NeedsUserLogin(string text) =>
        text.Contains("single sign-on", StringComparison.OrdinalIgnoreCase)
        || text.Contains("P4LOGINSSO", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Navigate to URL", StringComparison.OrdinalIgnoreCase)
        || text.Contains("second factor", StringComparison.OrdinalIgnoreCase)
        || text.Contains("multi-factor", StringComparison.OrdinalIgnoreCase)
        || text.Contains("multi factor", StringComparison.OrdinalIgnoreCase);
}
