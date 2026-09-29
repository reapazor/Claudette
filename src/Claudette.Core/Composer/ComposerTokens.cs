namespace Claudette.Core.Composer;

/// <summary>A token being completed in the composer: the slash command or <c>@</c> mention the caret is in.</summary>
/// <param name="Start">Where the token starts: the slash or the <c>@</c>.</param>
/// <param name="Length">Up to the caret.</param>
/// <param name="Query">What's typed after the slash or <c>@</c>, without an opening quote.</param>
public readonly record struct ComposerToken(int Start, int Length, string Query);

/// <summary>The composer's text after picking a completion, and where the caret goes.</summary>
public readonly record struct ComposerEdit(string Text, int Caret);

/// <summary>
/// Finds and replaces the tokens the composer completes (DESIGN.md §5, "Composer"): a slash command at the start of
/// the message, and <c>@path</c> mentions anywhere in it.
/// </summary>
public static class ComposerTokens
{
    /// <summary>How far back from the caret an <c>@</c> is looked for.</summary>
    private const int MaxMentionLength = 260;

    /// <summary>
    /// The slash command being typed: the message starts with <c>/</c> and the caret is still in its first word. Claude
    /// Code only runs a command at the start of a message.
    /// </summary>
    public static ComposerToken? FindSlashCommand(string text, int caret)
    {
        if (text.Length == 0 || text[0] != '/' || caret < 1 || caret > text.Length)
        {
            return null;
        }
        for (var i = 1; i < caret; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return null;
            }
        }
        return new ComposerToken(0, caret, text[1..caret]);
    }

    /// <summary>
    /// The <c>@</c> mention being typed: an <c>@</c> at the start or after whitespace (so an email address isn't one),
    /// then the path up to the caret. A path with spaces is quoted, <c>@"my file.txt"</c>, as Claude Code expects.
    /// </summary>
    public static ComposerToken? FindMention(string text, int caret)
    {
        if (caret < 1 || caret > text.Length)
        {
            return null;
        }
        var floor = Math.Max(0, caret - MaxMentionLength);
        for (var at = caret - 1; at >= floor; at--)
        {
            var c = text[at];
            if (c == '@' && (at == 0 || char.IsWhiteSpace(text[at - 1])))
            {
                var typed = text[(at + 1)..caret];
                if (typed.StartsWith('"'))
                {
                    // Quoted: spaces are part of the path, and a closing quote ends the mention.
                    return typed.IndexOf('"', 1) >= 0 || typed.Contains('\n') ? null : new ComposerToken(at, caret - at, typed[1..]);
                }
                return typed.Any(char.IsWhiteSpace) ? null : new ComposerToken(at, caret - at, typed);
            }
            if (c == '\n')
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Replaces the slash command token with <paramref name="name"/> and a space. The rest of the first word, after the
    /// caret, is replaced too.
    /// </summary>
    public static ComposerEdit ReplaceSlashCommand(string text, ComposerToken token, string name)
    {
        var end = token.Start + token.Length;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }
        var rest = text[end..];
        var inserted = "/" + name + " ";
        if (rest.StartsWith(' '))
        {
            rest = rest[1..];
        }
        return new ComposerEdit(inserted + rest, inserted.Length);
    }

    /// <summary>
    /// Replaces the mention token with <paramref name="path"/>. A folder (ending in <c>/</c>) is left open, without a
    /// space after it, so its contents can be picked next; a file gets a space.
    /// </summary>
    public static ComposerEdit ReplaceMention(string text, ComposerToken token, string path)
    {
        var end = token.Start + token.Length;
        var isFolder = path.EndsWith('/');
        var mention = Mention(path, closeQuote: !isFolder);
        var suffix = isFolder ? "" : " ";
        var rest = text[end..];
        if (!isFolder && rest.StartsWith(' '))
        {
            rest = rest[1..];
        }
        var before = text[..token.Start];
        return new ComposerEdit(before + mention + suffix + rest, before.Length + mention.Length + suffix.Length);
    }

    /// <summary>
    /// <c>@path</c>, or <c>@"path"</c> when it has whitespace. Paths use forward slashes, which Claude Code accepts on
    /// every OS.
    /// </summary>
    /// <param name="closeQuote">False leaves a quoted folder open, so the path can be continued.</param>
    public static string Mention(string path, bool closeQuote = true)
    {
        if (Path.DirectorySeparatorChar == '\\')
        {
            path = path.Replace('\\', '/');
        }
        if (!path.Any(char.IsWhiteSpace))
        {
            return "@" + path;
        }
        return closeQuote ? $"@\"{path}\"" : $"@\"{path}";
    }
}
