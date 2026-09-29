using System.Globalization;
using System.Text.RegularExpressions;
using Claudette.Core.Processes;

namespace Claudette.Core.Perforce;

public enum TicketState
{
    /// <summary>Logged in; <see cref="TicketStatus.ExpiresIn"/> says for how long, when Perforce reports it.</summary>
    Valid,
    /// <summary>"Your session has expired, please login again."</summary>
    Expired,
    /// <summary>"Perforce password (P4PASSWD) invalid or unset.": no ticket at all.</summary>
    NotLoggedIn,
    /// <summary>"'login' not necessary, no password set for this user."</summary>
    NotNeeded,
    /// <summary>The server couldn't be reached; there's nothing to renew until it can.</summary>
    Unreachable,
    /// <summary>Output Claudette doesn't recognize, or <c>p4</c> couldn't run.</summary>
    Unknown,
}

/// <summary>What <c>p4 -ztag login -s</c> said about the ticket (DESIGN.md §18, "Keeping the ticket fresh").</summary>
/// <param name="Message">Perforce's own words, for the tab's info card.</param>
public sealed partial record TicketStatus(TicketState State, TimeSpan? ExpiresIn, string Message)
{
    /// <summary>The ticket has expired, doesn't exist, or runs out within <paramref name="renewBefore"/>.</summary>
    public bool NeedsLogin(TimeSpan renewBefore) =>
        State is TicketState.Expired or TicketState.NotLoggedIn
        || State == TicketState.Valid && ExpiresIn is { } left && left < renewBefore;

    public static TicketStatus Parse(ProcessResult result)
    {
        var text = $"{result.StandardOutput}\n{result.StandardError}";
        var message = FirstLine(text);
        var fields = ZTag.ParseSingle(result.StandardOutput);
        if (result.ExitCode == 0
            && (fields.GetValueOrDefault("TicketExpiration") ?? fields.GetValueOrDefault("Expiration")) is { } seconds
            && long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            var expires = TimeSpan.FromSeconds(s);
            return new TicketStatus(TicketState.Valid, expires, fields.TryGetValue("User", out var user) ? $"User {user} ticket expires in {Duration(expires)}." : message);
        }
        if (ExpiresPattern().Match(text) is { Success: true } expiresText && (expiresText.Groups["h"].Success || expiresText.Groups["m"].Success))
        {
            var hours = expiresText.Groups["h"].Success ? int.Parse(expiresText.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
            var minutes = expiresText.Groups["m"].Success ? int.Parse(expiresText.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            return new TicketStatus(TicketState.Valid, new TimeSpan(hours, minutes, 0), message);
        }
        if (text.Contains("does not expire", StringComparison.OrdinalIgnoreCase))
        {
            return new TicketStatus(TicketState.Valid, null, message);
        }
        if (PerforceErrors.IsSessionExpired(text))
        {
            return new TicketStatus(TicketState.Expired, null, message);
        }
        if (PerforceErrors.IsPasswordUnset(text))
        {
            return new TicketStatus(TicketState.NotLoggedIn, null, message);
        }
        if (text.Contains("'login' not necessary", StringComparison.OrdinalIgnoreCase))
        {
            return new TicketStatus(TicketState.NotNeeded, null, message);
        }
        if (PerforceErrors.IsConnectionFailure(text))
        {
            return new TicketStatus(TicketState.Unreachable, null, message);
        }
        return result.ExitCode == 0
            ? new TicketStatus(TicketState.Valid, null, message)
            : new TicketStatus(TicketState.Unknown, null, message.Length > 0 ? message : $"p4 login -s failed (exit {result.ExitCode}).");
    }

    /// <summary>"11h", "45m", "2h 5m": short, for the info card.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        var hours = (int)span.TotalHours;
        return hours >= 10 || hours > 0 && span.Minutes == 0 ? $"{hours}h"
            : hours > 0 ? $"{hours}h {span.Minutes}m"
            : $"{Math.Max(span.Minutes, span > TimeSpan.Zero ? 1 : 0)}m";
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("... ", StringComparison.Ordinal)) ?? "";

    /// <summary>"User matt ticket expires in 11 hours 59 minutes." (also "1 hour", or minutes only).</summary>
    [GeneratedRegex(@"expires in (?:(?<h>\d+) hours?)?\s*(?:(?<m>\d+) minutes?)?", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiresPattern();
}
