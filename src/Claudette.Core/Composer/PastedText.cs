using System.Globalization;
using System.Text;

namespace Claudette.Core.Composer;

/// <summary>
/// A large paste kept as an attachment rather than put in the composer's box (DESIGN.md §5, "Attachments"): text over
/// <see cref="LargeBytes"/>, such as a log, which would make the box slow and hard to edit around. It goes with the
/// message, after what was typed.
/// </summary>
public static class PastedText
{
    /// <summary>A paste longer than this, in UTF-8, becomes an attachment.</summary>
    public const int LargeBytes = 32 * 1024;

    /// <summary>How many of its lines the attachment's tooltip shows.</summary>
    public const int PreviewLines = 12;

    /// <summary>Over <see cref="LargeBytes"/> in UTF-8. A UTF-16 character is at most 3 bytes there, so a short paste isn't counted.</summary>
    public static bool IsLarge(string? text) => text is not null && text.Length > LargeBytes / 3 && Encoding.UTF8.GetByteCount(text) > LargeBytes;

    /// <summary>What the attachment says: <c>Pasted text · 1,204 lines · 48 KB</c>.</summary>
    public static string Describe(string text)
    {
        var lines = CountLines(text);
        var kilobytes = (int)Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 1024.0);
        return string.Create(CultureInfo.CurrentCulture, $"Pasted text · {lines:N0} {(lines == 1 ? "line" : "lines")} · {kilobytes:N0} KB");
    }

    /// <summary>The first lines, for the attachment's tooltip, with an ellipsis when there's more.</summary>
    public static string Preview(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var shown = string.Join("\n", lines.Take(PreviewLines).Select(l => l.Length > 200 ? l[..200] + "…" : l));
        return lines.Length > PreviewLines ? shown + "\n…" : shown;
    }

    /// <summary>
    /// The message Claude gets: what was typed, then each paste, each after a blank line. One text block, so an
    /// <c>@path</c> in what was typed is still read.
    /// </summary>
    public static string Join(string typed, IReadOnlyList<string> pastes)
    {
        if (pastes.Count == 0)
        {
            return typed;
        }
        var parts = new List<string>(pastes.Count + 1);
        if (typed.Length > 0)
        {
            parts.Add(typed);
        }
        parts.AddRange(pastes.Select(p => p.TrimEnd('\r', '\n')));
        return string.Join("\n\n", parts);
    }

    private static int CountLines(string text)
    {
        var trimmed = text.TrimEnd('\r', '\n');
        return trimmed.Length == 0 ? 0 : trimmed.Count(c => c == '\n') + 1;
    }
}
