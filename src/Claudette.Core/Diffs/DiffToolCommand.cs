using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Claudette.Core.Diffs;

/// <summary>The values for a diff tool's placeholders.</summary>
/// <param name="Left">The "before" file: <c>{left}</c>.</param>
/// <param name="Right">The file as it is now: <c>{right}</c>.</param>
/// <param name="LeftTitle">For example <c>auth.cs (before)</c>: <c>{leftTitle}</c>.</param>
/// <param name="RightTitle">For example <c>auth.cs (now)</c>: <c>{rightTitle}</c>.</param>
public sealed record DiffToolSides(string Left, string Right, string LeftTitle, string RightTitle);

/// <summary>
/// A custom diff tool command (DESIGN.md §8): the program, and the argument template that follows it on the line the
/// user typed.
/// </summary>
public sealed record DiffToolCommand(string Program, IReadOnlyList<string> ArgumentTemplate)
{
    /// <summary>
    /// Parses a command line such as
    /// <c>"C:\Program Files\Beyond Compare 5\BCompare.exe" "{left}" "{right}" /title1="{leftTitle}"</c>.
    /// When the arguments mention neither <c>{left}</c> nor <c>{right}</c>, both are added at the end, so a bare
    /// program path works too.
    /// </summary>
    public static bool TryParse(string? commandLine, [NotNullWhen(true)] out DiffToolCommand? command, [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            error = "Enter the diff tool's command.";
            return false;
        }
        if (!DiffToolTemplate.TryTokenize(commandLine, out var tokens, out error))
        {
            return false;
        }
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
        {
            error = "The command doesn't start with a program.";
            return false;
        }

        var template = tokens.Skip(1).ToList();
        if (!template.Any(t => DiffToolTemplate.Mentions(t, "left") || DiffToolTemplate.Mentions(t, "right")))
        {
            template.Add("{left}");
            template.Add("{right}");
        }
        command = new DiffToolCommand(tokens[0], template);
        return true;
    }

    public IReadOnlyList<string> BuildArguments(DiffToolSides sides) => DiffToolTemplate.Substitute(ArgumentTemplate, sides);
}

/// <summary>
/// Splits and fills in diff tool argument templates. Tokens are split first, respecting double quotes, and the
/// placeholders are substituted inside each token afterwards, so a path with spaces stays one argument. The tokens
/// then go to the process unquoted, one per argument.
/// </summary>
public static partial class DiffToolTemplate
{
    /// <summary>
    /// Splits on whitespace outside double quotes. Quotes group and are removed, even mid-token
    /// (<c>/title1="a b"</c> is <c>/title1=a b</c>); <c>""</c> is an empty argument. Backslashes are ordinary
    /// characters, so Windows paths need no escaping. Unbalanced quotes are an error.
    /// </summary>
    public static bool TryTokenize(string text, out IReadOnlyList<string> tokens, [NotNullWhen(false)] out string? error)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var inToken = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                inToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(ch);
                inToken = true;
            }
        }
        if (inQuotes)
        {
            tokens = [];
            error = "A double quote isn't closed.";
            return false;
        }
        if (inToken)
        {
            result.Add(current.ToString());
        }
        tokens = result;
        error = null;
        return true;
    }

    /// <summary>
    /// Replaces <c>{left}</c>, <c>{right}</c>, <c>{leftTitle}</c> and <c>{rightTitle}</c> (in any letter case) in each
    /// token. Other text in braces is left alone, and substituted values aren't scanned again.
    /// </summary>
    public static IReadOnlyList<string> Substitute(IReadOnlyList<string> template, DiffToolSides sides) =>
        template.Select(token => Placeholder().Replace(token, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "left" => sides.Left,
            "right" => sides.Right,
            "lefttitle" => sides.LeftTitle,
            _ => sides.RightTitle,
        })).ToArray();

    internal static bool Mentions(string token, string placeholder) =>
        token.Contains("{" + placeholder + "}", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{(leftTitle|rightTitle|left|right)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
