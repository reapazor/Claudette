using System.Collections.Concurrent;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Claudette.App.Diffs;

/// <summary>A run of text with one foreground color (a <c>#RRGGBB</c> string), or null for the default color.</summary>
public readonly record struct ColoredRun(int Start, int Length, string? Color);

/// <summary>
/// Syntax highlighting for the diff view (DESIGN.md §8), with the TextMate grammars LiveMarkdown already ships for
/// code blocks. Highlights whole files, so multi-line constructs such as comments color correctly.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Bigger files are shown without colors, to keep the diff view quick.</summary>
    public const int MaxLines = 20_000;

    private static readonly TimeSpan LineTimeLimit = TimeSpan.FromMilliseconds(50);
    private static readonly ConcurrentDictionary<bool, (RegistryOptions Options, Registry Registry, Theme Theme)> Themes = new();
    private static readonly Lock GrammarLock = new();

    /// <summary>Colored runs for each line, or null when the file type isn't known or the file is too big.</summary>
    public static IReadOnlyList<IReadOnlyList<ColoredRun>>? Highlight(IReadOnlyList<string> lines, string fileName, bool dark)
    {
        if (lines.Count == 0 || lines.Count > MaxLines)
        {
            return null;
        }
        try
        {
            var (options, registry, theme) = Themes.GetOrAdd(dark, isDark =>
            {
                var o = new RegistryOptions(isDark ? ThemeName.DarkPlus : ThemeName.LightPlus);
                var r = new Registry(o);
                return (o, r, r.GetTheme());
            });
            var extension = Path.GetExtension(fileName);
            if (extension.Length == 0 || options.GetScopeByExtension(extension) is not { Length: > 0 } scope)
            {
                return null;
            }
            IGrammar? grammar;
            lock (GrammarLock)
            {
                grammar = registry.LoadGrammar(scope);
            }
            if (grammar is null)
            {
                return null;
            }

            var result = new List<IReadOnlyList<ColoredRun>>(lines.Count);
            IStateStack? state = null;
            foreach (var line in lines)
            {
                var tokens = grammar.TokenizeLine(line, state, LineTimeLimit);
                state = tokens.RuleStack;
                var runs = new List<ColoredRun>(tokens.Tokens.Length);
                foreach (var token in tokens.Tokens)
                {
                    var start = Math.Min(token.StartIndex, line.Length);
                    var end = Math.Min(token.EndIndex, line.Length);
                    if (end <= start)
                    {
                        continue;
                    }
                    var color = Foreground(theme, token.Scopes);
                    // Merge neighbors with the same color, so each line has few runs.
                    if (runs.Count > 0 && runs[^1].Color == color && runs[^1].Start + runs[^1].Length == start)
                    {
                        runs[^1] = runs[^1] with { Length = end - runs[^1].Start };
                    }
                    else
                    {
                        runs.Add(new ColoredRun(start, end - start, color));
                    }
                }
                result.Add(runs);
            }
            return result;
        }
        catch (Exception)
        {
            // A grammar that fails shows the file uncolored rather than not at all.
            return null;
        }
    }

    private static string? Foreground(Theme theme, IList<string> scopes)
    {
        foreach (var rule in theme.Match(scopes))
        {
            if (rule.foreground > 0)
            {
                return theme.GetColor(rule.foreground);
            }
        }
        return null;
    }
}
