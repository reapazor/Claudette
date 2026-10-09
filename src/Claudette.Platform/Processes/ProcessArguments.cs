using System.Text;

namespace Claudette.Platform.Processes;

/// <summary>
/// A process's arguments cut short for its row in the process monitor (DESIGN.md §4, "Process monitor"): without the
/// program, which the row names, and with each path down to its last part, on one line.
/// </summary>
public static class ProcessArguments
{
    /// <summary>Longer than a row could show at any width; the row ends it in "…" where it runs out of room.</summary>
    private const int MaxLength = 200;

    /// <summary>
    /// <paramref name="commandLine"/>'s arguments, short: <c>node C:\app\node_modules\vite\bin\vite.js --port 5173</c> is
    /// <c>vite.js --port 5173</c>. Null when it has none, or wasn't read.
    /// </summary>
    /// <param name="executablePath">The program's path, which a command line may start with unquoted, spaces and all.</param>
    public static string? Short(string? commandLine, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }
        var arguments = Words(WithoutProgram(commandLine.Trim(), executablePath)).Select(Shorten).Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a);
        var text = string.Join(' ', arguments);
        return text.Length == 0 ? null : text.Length > MaxLength ? text[..MaxLength] + "…" : text;
    }

    /// <summary>What follows the program: a quoted first word, the executable's path, or else the first word.</summary>
    private static string WithoutProgram(string commandLine, string? executablePath)
    {
        if (commandLine[0] == '"')
        {
            var end = commandLine.IndexOf('"', 1);
            return end < 0 ? "" : commandLine[(end + 1)..];
        }
        if (!string.IsNullOrEmpty(executablePath) && commandLine.StartsWith(executablePath, StringComparison.OrdinalIgnoreCase)
            && (commandLine.Length == executablePath.Length || char.IsWhiteSpace(commandLine[executablePath.Length])))
        {
            return commandLine[executablePath.Length..];
        }
        var space = commandLine.IndexOfAny([' ', '\t', '\n', '\r']);
        return space < 0 ? "" : commandLine[space..];
    }

    /// <summary>The words, split at white space outside double quotes, without the quotes.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }
            }
            else
            {
                word.Append(c);
            }
        }
        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    /// <summary>
    /// A path down to its last part, as an option's value too (<c>--config=C:\app\vite.config.ts</c>). An option without
    /// one, an address and a path straight under the root (<c>/tmp</c>, or a Windows switch: <c>/d</c>) stay as they are.
    /// </summary>
    private static string Shorten(string argument)
    {
        if (argument.Contains("://", StringComparison.Ordinal))
        {
            return argument;
        }
        var equals = argument.IndexOf('=');
        if (equals < 0 && argument.StartsWith('-'))
        {
            return argument;
        }
        var (option, value) = equals < 0 ? ("", argument) : (argument[..(equals + 1)], argument[(equals + 1)..]);
        var path = value.TrimEnd('/', '\\');
        var slash = path.LastIndexOfAny(['/', '\\']);
        return slash <= 0 ? argument : option + path[(slash + 1)..];
    }
}
