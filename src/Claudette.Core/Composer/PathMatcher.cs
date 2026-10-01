namespace Claudette.Core.Composer;

/// <summary>
/// Ranks a working folder's paths against what's typed after <c>@</c> (DESIGN.md §5, "Composer"). Case doesn't
/// matter. Best first:
/// <list type="number">
/// <item>the file or folder name starts with the query;</item>
/// <item>the path starts with it (so <c>src/de</c> finds <c>src/deep/</c>);</item>
/// <item>a path segment starts with it;</item>
/// <item>the name, then the path, contains it;</item>
/// <item>its characters appear in order, preferring the name and the starts of segments (<c>tvm</c> finds
/// <c>TabViewModel.cs</c>).</item>
/// </list>
/// Ties go to shallower, then shorter, paths. With no query, the top of the folder is listed, folders first.
/// </summary>
public static class PathMatcher
{
    /// <param name="cancellationToken">Cancelled when another keystroke makes this match stale.</param>
    public static IReadOnlyList<IndexedPath> Match(IReadOnlyList<IndexedPath> paths, string query, int limit = 50, CancellationToken cancellationToken = default)
    {
        query = query.Replace('\\', '/');
        if (query.Length == 0)
        {
            return paths.Where(p => p.Depth == 0)
                .OrderBy(p => p.IsFolder ? 0 : 1)
                .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();
        }

        var scored = new List<(IndexedPath Path, int Score)>();
        for (var i = 0; i < paths.Count; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var path = paths[i];
            // Having picked a folder, list what's in it rather than the folder again.
            if (query.EndsWith('/') && path.Path.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var score = Score(path, query);
            if (score > 0)
            {
                scored.Add((path, score));
            }
        }
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Path.Depth)
            .ThenBy(s => s.Path.Path.Length)
            .ThenBy(s => s.Path.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(s => s.Path)
            .ToArray();
    }

    /// <summary>Higher is better; 0 is no match.</summary>
    internal static int Score(IndexedPath path, string query)
    {
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        var text = path.Path;
        var name = path.Name;
        if (!query.Contains('/') && name.StartsWith(query, ignoreCase))
        {
            return 1000;
        }
        if (text.StartsWith(query, ignoreCase))
        {
            return 900;
        }
        var index = text.IndexOf(query, ignoreCase);
        if (index > 0 && text[index - 1] == '/')
        {
            return 800;
        }
        if (!query.Contains('/') && name.Contains(query, ignoreCase))
        {
            return 600;
        }
        if (index >= 0)
        {
            return 500;
        }
        var nameStart = text.Length - name.Length - (path.IsFolder ? 1 : 0);
        // Jumping to word starts reads better (tvm → TabViewModel) but can skip letters a later character needs, so a
        // plain left-to-right match is the fallback.
        return Math.Max(Fuzzy(text, nameStart, query, preferBoundaries: true), Fuzzy(text, nameStart, query, preferBoundaries: false));
    }

    /// <summary>
    /// The query's characters in order, anywhere in the path: 1 to 400, more for matches at the start of a segment or
    /// a word (after <c>.</c>, <c>-</c>, <c>_</c> or a lower-to-upper case change), in the name, and next to each other.
    /// </summary>
    private static int Fuzzy(string text, int nameStart, string query, bool preferBoundaries)
    {
        var score = 0;
        var position = 0;
        var previous = -2;
        foreach (var q in query)
        {
            var found = -1;
            if (preferBoundaries)
            {
                var next = NextMatch(text, position, q);
                // Keep a run going; otherwise take the next word start that matches.
                found = next == previous + 1 ? next : NextBoundaryMatch(text, position, q);
                if (found < 0)
                {
                    found = next;
                }
            }
            else
            {
                found = NextMatch(text, position, q);
            }
            if (found < 0)
            {
                return 0;
            }
            score += 10;
            if (IsBoundary(text, found))
            {
                score += 15;
            }
            if (found == previous + 1)
            {
                score += 10;
            }
            if (found >= nameStart)
            {
                score += 5;
            }
            previous = found;
            position = found + 1;
        }
        return Math.Clamp(score - (text.Length / 10), 1, 400);
    }

    private static int NextMatch(string text, int from, char q)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(q))
            {
                return i;
            }
        }
        return -1;
    }

    private static int NextBoundaryMatch(string text, int from, char q)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(q) && IsBoundary(text, i))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsBoundary(string text, int i) =>
        i == 0 || text[i - 1] is '/' or '.' or '-' or '_' or ' ' || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}
