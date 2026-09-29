using System.Globalization;
using System.Text.RegularExpressions;

namespace Claudette.Core.Perforce;

public enum ChangelistState
{
    Pending,
    /// <summary>"Change 12345 submitted.": the badge says "submitted".</summary>
    Submitted,
    /// <summary>"Change 12345 deleted.": the badge goes away, and the info card no longer lists it.</summary>
    Deleted,
}

/// <summary>A changelist Claude used in this tab's session. Saved with the tab (DESIGN.md §18).</summary>
public sealed class TrackedChangelist
{
    public long Number { get; set; }

    public ChangelistState State { get; set; }

    /// <summary>When a command or its output last named it: the most recent one is the tab's changelist.</summary>
    public DateTimeOffset LastUsed { get; set; }
}

/// <summary>
/// Follows which Perforce changelist Claude is working in, from its Bash commands and their output (DESIGN.md §18,
/// "Perforce changelist in the tab title"). Commands that name a changelist (<c>p4 edit -c 12345</c>, <c>add</c>,
/// <c>reopen</c>, <c>shelve</c>, <c>submit -c</c>, <c>change -o 12345</c>…) and output that creates, submits or deletes
/// one (<c>Change 12345 created.</c>) update it. The most recently used one wins; the default changelist is ignored.
/// Works on the tab's saved list, so it survives restarts.
/// </summary>
public sealed partial class ChangelistTracker(List<TrackedChangelist> changelists)
{
    /// <summary>Subcommands whose <c>-c</c> names the changelist they open files in (or submit).</summary>
    private static readonly HashSet<string> ChangelistCommands = new(StringComparer.Ordinal)
    {
        "edit", "add", "delete", "reopen", "shelve", "unshelve", "submit", "integrate", "integ", "copy", "merge",
        "move", "rename", "undo", "lock", "resolve",
    };

    /// <summary>The tab's changelist: the most recently used one, or null when it was deleted or there's none.</summary>
    public TrackedChangelist? Current => Latest() is { State: not ChangelistState.Deleted } latest ? latest : null;

    /// <summary>Every changelist used in the session that still exists, most recent first.</summary>
    public IReadOnlyList<TrackedChangelist> All => Recent().Where(c => c.State != ChangelistState.Deleted).ToArray();

    /// <summary>
    /// A Bash command finished. Returns whether anything changed. Only <c>p4</c> commands count; a command that
    /// failed still counts for what its output says happened, but not for the changelist it named.
    /// </summary>
    public bool Observe(string? command, string output, bool isError, DateTimeOffset at)
    {
        var invocations = PerforceCommands.Find(command);
        if (invocations.Count == 0)
        {
            return false;
        }
        var changed = false;
        if (!isError)
        {
            foreach (var invocation in invocations)
            {
                if (NamedChangelist(invocation) is { } number)
                {
                    changed |= Use(number, at);
                }
            }
        }
        foreach (Match match in OutputPattern().Matches(output))
        {
            var number = long.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            switch (match.Groups["what"].Value)
            {
                case "created" or "updated":
                    changed |= Use(number, at);
                    break;
                case "submitted":
                    changed |= Set(number, ChangelistState.Submitted, at);
                    break;
                case "deleted":
                    changed |= Set(number, ChangelistState.Deleted, at);
                    break;
                default:
                    // "Change 100 renamed change 105 and submitted.": the pending number became the submitted one.
                    if (long.TryParse(match.Groups["to"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var renamed))
                    {
                        changelists.RemoveAll(c => c.Number == number);
                        Set(renamed, ChangelistState.Submitted, at);
                        changed = true;
                    }
                    break;
            }
        }
        return changed;
    }

    /// <summary>The changelist a single <c>p4</c> command works in, or null for none or the default changelist.</summary>
    public static long? NamedChangelist(PerforceInvocation invocation)
    {
        string? value = null;
        if (ChangelistCommands.Contains(invocation.Subcommand))
        {
            value = PerforceCommands.OptionValue(invocation.Arguments, "-c");
        }
        else if (invocation.Subcommand is "change" or "changelist")
        {
            // p4 change -o 12345 (or p4 change 12345 to edit it); p4 change -d is handled by its output.
            if (PerforceCommands.OptionValue(invocation.Arguments, "-d") is null)
            {
                value = invocation.Arguments.LastOrDefault(a => !a.StartsWith('-'));
            }
        }
        return value is not null && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    private bool Use(long number, DateTimeOffset at)
    {
        var existing = changelists.Find(c => c.Number == number);
        if (existing is null)
        {
            changelists.Add(new TrackedChangelist { Number = number, State = ChangelistState.Pending, LastUsed = at });
            return true;
        }
        var changed = existing.State == ChangelistState.Deleted || Latest() != existing;
        if (existing.State == ChangelistState.Deleted)
        {
            existing.State = ChangelistState.Pending;
        }
        Touch(existing, at);
        return changed;
    }

    private bool Set(long number, ChangelistState state, DateTimeOffset at)
    {
        var existing = changelists.Find(c => c.Number == number);
        if (existing is null)
        {
            if (state == ChangelistState.Deleted)
            {
                return false;
            }
            changelists.Add(new TrackedChangelist { Number = number, State = state, LastUsed = at });
            return true;
        }
        var changed = existing.State != state || Latest() != existing;
        existing.State = state;
        Touch(existing, at);
        return changed;
    }

    /// <summary>Most recent first. The list is kept in the order they were last used, which breaks ties in time.</summary>
    private IEnumerable<TrackedChangelist> Recent() =>
        changelists.Select((c, i) => (Changelist: c, Index: i)).OrderByDescending(e => e.Changelist.LastUsed).ThenByDescending(e => e.Index).Select(e => e.Changelist);

    private TrackedChangelist? Latest() => Recent().FirstOrDefault();

    private void Touch(TrackedChangelist changelist, DateTimeOffset at)
    {
        changelist.LastUsed = at;
        changelists.Remove(changelist);
        changelists.Add(changelist);
    }

    [GeneratedRegex(@"\bChange (?<n>\d+) (?:(?<what>created|updated|submitted|deleted)\b|renamed change (?<to>\d+) and submitted)")]
    private static partial Regex OutputPattern();
}
