using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.App.Conversation;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    /// <summary>A note such as "… 120 more lines".</summary>
    Note,
}

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber)
{
    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        _ => " ",
    };

    public string OldNumberText => OldNumber?.ToString() ?? "";

    public string NewNumberText => NewNumber?.ToString() ?? "";

    public bool IsAdded => Kind == DiffLineKind.Added;

    public bool IsRemoved => Kind == DiffLineKind.Removed;

    public bool IsHunk => Kind is DiffLineKind.Hunk or DiffLineKind.Note;
}

/// <summary>A diff for a card, with its <c>+added −removed</c> counts.</summary>
public sealed record DiffView(IReadOnlyList<DiffLine> Lines, int Added, int Removed)
{
    /// <summary>Very large diffs are cut short in the card; the full diff view comes in milestone 6.</summary>
    public const int MaxLines = 400;

    public string Stats => $"+{Added} −{Removed}";

    /// <summary>
    /// From Claude Code's <c>structuredPatch</c> (DESIGN.md §8): hunks with <c>oldStart</c>, <c>newStart</c> and
    /// <c>lines</c> prefixed with ' ', '-' or '+'.
    /// </summary>
    public static DiffView? FromStructuredPatch(JsonNode? patch)
    {
        if (patch is not JsonArray hunks || hunks.Count == 0)
        {
            return null;
        }
        var lines = new List<DiffLine>();
        int added = 0, removed = 0;
        foreach (var hunk in hunks.OfType<JsonObject>())
        {
            var oldLine = Number(hunk["oldStart"]);
            var newLine = Number(hunk["newStart"]);
            lines.Add(new DiffLine(DiffLineKind.Hunk, $"@@ -{oldLine},{Number(hunk["oldLines"])} +{newLine},{Number(hunk["newLines"])} @@", null, null));
            foreach (var entry in (hunk["lines"] as JsonArray ?? []).OfType<JsonValue>())
            {
                if (!entry.TryGetValue<string>(out var text) || text.Length == 0)
                {
                    continue;
                }
                var body = text[1..];
                switch (text[0])
                {
                    case '+':
                        added++;
                        lines.Add(new DiffLine(DiffLineKind.Added, body, null, newLine++));
                        break;
                    case '-':
                        removed++;
                        lines.Add(new DiffLine(DiffLineKind.Removed, body, oldLine++, null));
                        break;
                    case '\\':
                        break; // "\ No newline at end of file"
                    default:
                        lines.Add(new DiffLine(DiffLineKind.Context, body, oldLine++, newLine++));
                        break;
                }
            }
        }
        return new DiffView(Cap(lines), added, removed);
    }

    /// <summary>Before the tool has run: an Edit's <c>old_string</c> as removed lines and <c>new_string</c> as added.</summary>
    public static DiffView FromReplacement(string oldText, string newText)
    {
        var removed = SplitLines(oldText);
        var added = SplitLines(newText);
        var lines = removed.Select(l => new DiffLine(DiffLineKind.Removed, l, null, null))
            .Concat(added.Select(l => new DiffLine(DiffLineKind.Added, l, null, null)))
            .ToList();
        return new DiffView(Cap(lines), added.Length, removed.Length);
    }

    /// <summary>A new file: every line added.</summary>
    public static DiffView FromNewFile(string content)
    {
        var added = SplitLines(content);
        return new DiffView(Cap(added.Select((l, i) => new DiffLine(DiffLineKind.Added, l, null, i + 1)).ToList()), added.Length, 0);
    }

    private static List<DiffLine> Cap(List<DiffLine> lines)
    {
        if (lines.Count <= MaxLines)
        {
            return lines;
        }
        var capped = lines.Take(MaxLines).ToList();
        capped.Add(new DiffLine(DiffLineKind.Note, $"… {lines.Count - MaxLines} more lines", null, null));
        return capped;
    }

    private static string[] SplitLines(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static int Number(JsonNode? node) => (int)Math.Clamp(node.AsWholeNumber() ?? 0, 0, int.MaxValue);
}
