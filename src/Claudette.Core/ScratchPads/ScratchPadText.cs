using System.Text;

namespace Claudette.Core.ScratchPads;

/// <summary>Where text added to a scratch pad goes (DESIGN.md §18, "Scratch pad").</summary>
/// <param name="Text">The whole pad afterwards.</param>
/// <param name="Start">Where the added text starts in it.</param>
/// <param name="Length">How long the added text is.</param>
public readonly record struct ScratchPadAddition(string Text, int Start, int Length);

/// <summary>The scratch pad's text (DESIGN.md §18, "Scratch pad"): lines end in <c>\n</c>, whatever the OS.</summary>
public static class ScratchPadText
{
    /// <summary><paramref name="text"/> with every line ending as <c>\n</c>.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// <paramref name="addition"/> at the end of <paramref name="pad"/>, after a blank line, and followed by a line break
    /// so typing carries on below it. Blank lines around the addition, and trailing spaces, are left out.
    /// </summary>
    public static ScratchPadAddition Append(string pad, string addition)
    {
        var added = Trim(Normalize(addition));
        var before = Normalize(pad).TrimEnd();
        var start = before.Length == 0 ? 0 : before.Length + 2;
        var text = before.Length == 0 ? added + "\n" : $"{before}\n\n{added}\n";
        return new ScratchPadAddition(text, start, added.Length);
    }

    /// <summary>
    /// Code as a Markdown code block in <paramref name="language"/>: fenced with more backticks than any run in the code,
    /// so code that has a fence of its own stays inside.
    /// </summary>
    public static string Fence(string code, string? language)
    {
        var body = Normalize(code).TrimEnd('\n');
        var longest = 0;
        var run = 0;
        foreach (var c in body)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{language?.Trim()}\n{body}\n{fence}";
    }

    /// <summary>
    /// <b>Keep both</b> for two machines' changes to a pad: what the two have the same at their start and end once,
    /// with this machine's lines between them and then the other machine's. Two notes added at the end of the same pad
    /// come out as the pad and both notes; nothing either machine wrote is lost.
    /// </summary>
    public static string KeepBoth(string mine, string theirs)
    {
        if (string.IsNullOrWhiteSpace(mine) || string.IsNullOrWhiteSpace(theirs))
        {
            return Normalize(string.IsNullOrWhiteSpace(mine) ? theirs : mine);
        }
        var a = Normalize(mine).TrimEnd('\n').Split('\n');
        var b = Normalize(theirs).TrimEnd('\n').Split('\n');
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }
        var lines = new List<string>(a.Length + b.Length);
        lines.AddRange(a[..prefix]);
        lines.AddRange(a[prefix..^suffix]);
        lines.AddRange(b[prefix..^suffix]);
        lines.AddRange(a[^suffix..]);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Without blank lines at the start and end, or spaces at the end; a first line's indent stays.</summary>
    private static string Trim(string text)
    {
        var lines = text.Split('\n');
        var first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first < 0)
        {
            return "";
        }
        var last = Array.FindLastIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        var builder = new StringBuilder();
        for (var i = first; i <= last; i++)
        {
            builder.Append(i == last ? lines[i].TrimEnd() : lines[i]);
            if (i < last)
            {
                builder.Append('\n');
            }
        }
        return builder.ToString();
    }
}
