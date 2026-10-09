using System.Text;
using Claudette.Core.Threads;

namespace Claudette.Core.Composer;

public enum MentionKind
{
    /// <summary>One of this thread's sub-threads (DESIGN.md §18, "Threads"): Claudette delivers what's sent to it.</summary>
    SubThread,

    /// <summary>The session in another of Claudette's tabs.</summary>
    Tab,

    /// <summary>Another Claude Code session on this machine: a terminal's, another app's.</summary>
    Session,

    /// <summary>One of this tab's subagents.</summary>
    Subagent,
}

/// <summary>Someone the composer's <c>@</c> can name, for Claude to message (DESIGN.md §5, "Autocomplete").</summary>
/// <param name="Name">What the list shows and the mention says: the tab's name, the session's, or the subagent's task.</param>
/// <param name="Address">What <c>SendMessage</c> takes as <c>to</c>: the session's name, or the subagent's agent ID.</param>
/// <param name="About">More for Claude to go on: a session's folder, or a subagent's type.</param>
public sealed record MentionTarget(string Name, string Address, MentionKind Kind, string? About = null);

/// <summary>A message with its mentions of sessions and agents resolved.</summary>
/// <param name="Text">The message as Claude gets it: each mention without its <c>@</c>, which Claude Code would take for a file.</param>
/// <param name="Mentioned">Who it names, in the order they first come.</param>
public sealed record ResolvedMentions(string Text, IReadOnlyList<MentionTarget> Mentioned);

/// <summary>
/// Mentions of other sessions and agents in the composer (DESIGN.md §5, "Autocomplete"): <c>@"Art page"</c> names the
/// session in the tab Art page. Claude gets the name without the <c>@</c>, and a note saying how to reach it.
/// </summary>
public static class SessionMentions
{
    /// <summary>The targets with names made unique, <c>(2)</c> after a name an earlier one has, as sub-threads' are.</summary>
    public static IReadOnlyList<MentionTarget> Unique(IReadOnlyList<MentionTarget> targets)
    {
        var names = ThreadNames.Unique([.. targets.Select(t => t.Name)]);
        return [.. targets.Select((t, i) => t with { Name = names[i] })];
    }

    /// <summary>
    /// The targets for what's typed after <c>@</c>, best first: a name that starts with it, then one with a word that
    /// does, then one that contains it, ignoring case. With nothing typed, the first <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<MentionTarget> Match(IReadOnlyList<MentionTarget> targets, string query, int max)
    {
        if (query.Length == 0)
        {
            return [.. targets.Take(max)];
        }
        return [.. targets
            .Select((target, order) => (target, order, rank: Rank(target, query)))
            .Where(m => m.rank >= 0)
            .OrderBy(m => m.rank)
            .ThenBy(m => m.order)
            .Take(max)
            .Select(m => m.target)];
    }

    private static int Rank(MentionTarget target, string query)
    {
        var name = target.Name;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        for (var i = name.IndexOf(query, StringComparison.OrdinalIgnoreCase); i > 0; i = name.IndexOf(query, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (!char.IsLetterOrDigit(name[i - 1]))
            {
                return 1;
            }
        }
        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
            : target.Address.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3
            : -1;
    }

    /// <summary>
    /// Finds the mentions of <paramref name="targets"/> in a message: <c>@name</c> or <c>@"a name"</c> at the start of a
    /// word, ignoring case, with any punctuation after an unquoted one (<c>@api-worker,</c>). Other mentions, of files,
    /// stay as they are.
    /// </summary>
    public static ResolvedMentions Resolve(string text, IReadOnlyList<MentionTarget> targets)
    {
        if (targets.Count == 0 || !text.Contains('@'))
        {
            return new ResolvedMentions(text, []);
        }
        var byName = new Dictionary<string, MentionTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            byName.TryAdd(target.Name, target);
        }
        var result = new StringBuilder(text.Length);
        var mentioned = new List<MentionTarget>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '@' && (i == 0 || char.IsWhiteSpace(text[i - 1])) && Named(text, i + 1, byName) is { } target)
            {
                if (!mentioned.Contains(target))
                {
                    mentioned.Add(target);
                }
                continue; // Drops the @.
            }
            result.Append(text[i]);
        }
        return new ResolvedMentions(mentioned.Count == 0 ? text : result.ToString(), mentioned);
    }

    private static MentionTarget? Named(string text, int start, Dictionary<string, MentionTarget> byName)
    {
        if (start >= text.Length)
        {
            return null;
        }
        if (text[start] == '"')
        {
            var close = text.IndexOf('"', start + 1);
            var line = text.IndexOf('\n', start);
            return close > start && (line < 0 || close < line) && byName.TryGetValue(text[(start + 1)..close], out var quoted) ? quoted : null;
        }
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }
        // Without the punctuation after it, one character at a time: "@api-worker," or "(ask @docs-page)".
        for (var word = text[start..end]; word.Length > 0; word = word[..^1])
        {
            if (byName.TryGetValue(word, out var named))
            {
                return named;
            }
            if (char.IsLetterOrDigit(word[^1]))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>What Claude is told after a message that names sessions or agents: who each is, and how to reach it.</summary>
    public static string Note(IReadOnlyList<MentionTarget> mentioned)
    {
        if (mentioned.Count == 1)
        {
            var target = mentioned[0];
            return $"[Claudette] \"{target.Name}\" in this message is {Describe(target)}. To message it, call SendMessage with to: \"{target.Address}\".";
        }
        var text = new StringBuilder("[Claudette] Who this message names, each of whom you can message with SendMessage:");
        foreach (var target in mentioned)
        {
            text.Append($"\n- \"{target.Name}\" is {Describe(target)}. Its to: \"{target.Address}\".");
        }
        return text.ToString();
    }

    private static string Describe(MentionTarget target) => target.Kind switch
    {
        MentionKind.SubThread => "one of this thread's sub-threads: Claudette delivers what you send it, and sends you its result when it finishes",
        MentionKind.Tab => "another Claude Code session on this machine, open in another of Claudette's tabs",
        MentionKind.Subagent => target.About is { Length: > 0 } type ? $"one of your subagents ({type})" : "one of your subagents",
        _ => target.About is { Length: > 0 } folder ? $"another Claude Code session on this machine, working in {folder}" : "another Claude Code session on this machine",
    };
}
