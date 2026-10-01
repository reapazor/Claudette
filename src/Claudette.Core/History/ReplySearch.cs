using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;
using Claudette.Core.Protocol;

namespace Claudette.Core.History;

/// <summary>
/// History's <b>Search Claude's replies too</b> (DESIGN.md §9, "History"): looks through the text of Claude's replies in
/// one transcript, on demand. Replies aren't kept in History's cache, which only holds prompts, so this reads the file;
/// it skips lines cheaply before parsing them, as the index does.
/// </summary>
public static class ReplySearch
{
    /// <summary>About how much of the reply a snippet shows on each side of the match.</summary>
    public const int SnippetContext = 60;

    private const string AssistantType = "\"type\":\"assistant\"";
    private const string TextBlock = "\"type\":\"text\"";
    private const string SidechainFlag = "\"isSidechain\":true";

    /// <summary>
    /// Whether every word of <paramref name="words"/> that isn't in <paramref name="knownText"/> (the title, prompts and
    /// folder search already looked through) is in one of Claude's replies, ignoring case. Returns a snippet of the
    /// first reply that has one of them, or null when some word is in neither. Subagents' replies don't count.
    /// </summary>
    public static string? Find(string transcriptPath, IReadOnlyList<string> words, string knownText, CancellationToken cancellationToken = default)
    {
        var missing = words.Where(w => !knownText.Contains(w, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0)
        {
            return null;
        }
        string? snippet = null;
        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains(AssistantType, StringComparison.Ordinal)
                    || !line.Contains(TextBlock, StringComparison.Ordinal)
                    || line.Contains(SidechainFlag, StringComparison.Ordinal)
                    // Every missing word is in the line somewhere, or it can't be in its text.
                    || !missing.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                foreach (var text in ReplyTexts(line))
                {
                    for (var i = missing.Count - 1; i >= 0; i--)
                    {
                        var at = text.IndexOf(missing[i], StringComparison.OrdinalIgnoreCase);
                        if (at < 0)
                        {
                            continue;
                        }
                        snippet ??= Snippet(text, at, missing[i].Length);
                        missing.RemoveAt(i);
                    }
                    if (missing.Count == 0)
                    {
                        return snippet;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone or locked: not a match this time.
        }
        return null;
    }

    private static IEnumerable<string> ReplyTexts(string line)
    {
        JsonObject? entry;
        try
        {
            entry = JsonTree.ParseObject(line);
        }
        catch (JsonException)
        {
            yield break;
        }
        if (entry?.GetString("type") != "assistant" || entry.GetBool("isSidechain") == true
            || entry.GetObject("message")?.GetArray("content") is not { } content)
        {
            yield break;
        }
        foreach (var block in content.OfType<JsonObject>())
        {
            if (block.GetString("type") == "text" && block.GetString("text") is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }

    /// <summary>The match with some text either side, on one line, with … where it was cut.</summary>
    internal static string Snippet(string text, int at, int length)
    {
        var start = Math.Max(0, at - SnippetContext);
        var end = Math.Min(text.Length, at + length + SnippetContext);
        // Don't split a surrogate pair at either end.
        if (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start--;
        }
        if (end < text.Length && char.IsLowSurrogate(text[end]))
        {
            end++;
        }
        var middle = string.Join(' ', text[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"{(start > 0 ? "…" : "")}{middle}{(end < text.Length ? "…" : "")}";
    }
}
