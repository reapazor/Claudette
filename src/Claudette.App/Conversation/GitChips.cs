using System.Globalization;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.App.Conversation;

/// <summary>One thing git did in a Bash command, for the chips on its card: its words, and the pull request's address.</summary>
public sealed record GitChip(string Text, string? Url)
{
    public bool HasUrl => Url is not null;
}

/// <summary>
/// The chips for a Bash result's <c>gitOperation</c> (documented: Tool Output Types, Bash): a commit, a push, a merge or
/// rebase, and what happened to a pull request. A part Claudette doesn't know, or an action, is left out or named plainly.
/// </summary>
public static class GitChips
{
    public static IReadOnlyList<GitChip> From(JsonObject? operation)
    {
        if (operation is null)
        {
            return [];
        }
        var chips = new List<GitChip>();
        if (operation.GetObject("commit") is { } commit && commit.GetString("sha") is { Length: > 0 } sha)
        {
            var verb = commit.GetString("kind") switch
            {
                "amended" => "Amended",
                "cherry-picked" => "Cherry-picked",
                _ => "Committed",
            };
            var on = commit.GetString("branch") is { Length: > 0 } branch ? $" on {branch}" : "";
            chips.Add(new GitChip($"{verb} {sha[..Math.Min(7, sha.Length)]}{on}", null));
        }
        if (operation.GetObject("push")?.GetString("branch") is { Length: > 0 } pushed)
        {
            chips.Add(new GitChip($"Pushed {pushed}", null));
        }
        if (operation.GetObject("branch") is { } branchOperation && branchOperation.GetString("ref") is { Length: > 0 } reference)
        {
            chips.Add(new GitChip(branchOperation.GetString("action") == "rebased" ? $"Rebased onto {reference}" : $"Merged {reference}", null));
        }
        if (operation.GetObject("pr") is { } pr && pr.GetDouble("number") is { } number)
        {
            var verb = pr.GetString("action") switch
            {
                "created" => "Opened",
                "edited" => "Edited",
                "merged" => "Merged",
                "commented" => "Commented on",
                "closed" => "Closed",
                "reopened" => "Reopened",
                "ready" => "Marked ready",
                "draft" => "Marked as draft",
                "auto-merge-enabled" => "Turned on auto-merge for",
                "auto-merge-disabled" => "Turned off auto-merge for",
                _ => "Pull request",
            };
            var url = pr.GetString("url") is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? address : null;
            var name = verb == "Pull request" ? "" : $"{verb} ";
            chips.Add(new GitChip($"{name}PR #{((long)number).ToString(CultureInfo.InvariantCulture)}", url));
        }
        return chips;
    }
}
