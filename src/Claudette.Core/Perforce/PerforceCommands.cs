using System.Text;

namespace Claudette.Core.Perforce;

/// <summary>One <c>p4</c> command inside a shell command line: its subcommand and that subcommand's arguments.</summary>
public sealed record PerforceInvocation(string Subcommand, IReadOnlyList<string> Arguments);

/// <summary>
/// Finds the <c>p4</c> commands in a Bash command line, such as <c>cd src &amp;&amp; p4 edit -c 12345 a.cpp</c>. A simple
/// shell tokenizer: quotes and backslashes are understood, and <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>,
/// <c>&amp;</c>, parentheses and line breaks separate commands. Leading <c>NAME=value</c> assignments are skipped.
/// </summary>
public static class PerforceCommands
{
    /// <summary>p4's global options that take a value (<c>-c client</c>, <c>-p port</c> and so on).</summary>
    private const string OptionsWithValue = "bcCdEHLpPQruvxz";

    public static bool RunsP4(string? commandLine) => Find(commandLine).Count > 0;

    public static IReadOnlyList<PerforceInvocation> Find(string? commandLine)
    {
        var invocations = new List<PerforceInvocation>();
        if (string.IsNullOrWhiteSpace(commandLine) || !commandLine.Contains("p4", StringComparison.OrdinalIgnoreCase))
        {
            return invocations;
        }
        foreach (var words in Commands(commandLine))
        {
            var i = 0;
            while (i < words.Count && IsAssignment(words[i]))
            {
                i++;
            }
            if (i >= words.Count || !IsP4(words[i]))
            {
                continue;
            }
            i++;
            // Global options come before the subcommand; -c there is the client, not a changelist.
            while (i < words.Count && words[i].StartsWith('-') && words[i].Length > 1)
            {
                var option = words[i];
                i++;
                if (option.Length == 2 && OptionsWithValue.Contains(option[1], StringComparison.Ordinal))
                {
                    i++;
                }
            }
            if (i < words.Count)
            {
                invocations.Add(new PerforceInvocation(words[i], words.Skip(i + 1).ToArray()));
            }
        }
        return invocations;
    }

    /// <summary>The value of <paramref name="option"/> (such as <c>-c</c>), given as <c>-c 12345</c> or <c>-c12345</c>.</summary>
    public static string? OptionValue(IReadOnlyList<string> arguments, string option)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument == "--")
            {
                break;
            }
            if (argument == option)
            {
                return i + 1 < arguments.Count ? arguments[i + 1] : null;
            }
            if (argument.Length > option.Length && argument.StartsWith(option, StringComparison.Ordinal) && option.Length == 2)
            {
                return argument[option.Length..];
            }
        }
        return null;
    }

    private static bool IsP4(string word)
    {
        var name = word.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Equals("p4", StringComparison.OrdinalIgnoreCase) || name.Equals("p4.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        return equals > 0 && word[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(word[0]);
    }

    /// <summary>The command line split into simple commands, each a list of words with quoting removed.</summary>
    private static List<List<string>> Commands(string text)
    {
        var commands = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\'':
                    inWord = true;
                    var close = text.IndexOf('\'', i + 1);
                    var end = close < 0 ? text.Length : close;
                    word.Append(text, i + 1, end - i - 1);
                    i = end;
                    break;
                case '"':
                    inWord = true;
                    for (i++; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] is '"' or '\\' or '$' or '`'))
                        {
                            i++;
                        }
                        word.Append(text[i]);
                    }
                    break;
                case '\\' when i + 1 < text.Length:
                    inWord = true;
                    i++;
                    if (text[i] != '\n')
                    {
                        word.Append(text[i]);
                    }
                    break;
                case ' ' or '\t' or '\r':
                    EndWord();
                    break;
                case '\n' or ';' or '&' or '|' or '(' or ')':
                    EndWord();
                    EndCommand();
                    break;
                default:
                    inWord = true;
                    word.Append(c);
                    break;
            }
        }
        EndWord();
        EndCommand();
        return commands;

        void EndWord()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }

        void EndCommand()
        {
            if (words.Count > 0)
            {
                commands.Add(words);
                words = [];
            }
        }
    }
}
