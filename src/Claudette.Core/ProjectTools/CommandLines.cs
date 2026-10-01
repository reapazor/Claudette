using System.Text;
using Claudette.Core.Processes;

namespace Claudette.Core.ProjectTools;

/// <summary>
/// Builds the commands project actions run (DESIGN.md §18, "Project tools"): <c>.bat</c> files through
/// <c>cmd.exe</c>, <c>.sh</c> files through <c>/bin/bash</c>, and the user's own commands through their shell.
/// </summary>
public static class CommandLines
{
    public const string Cmd = "cmd.exe";

    public const string Bash = "/bin/bash";

    public const string DefaultShell = "/bin/sh";

    /// <summary>
    /// Runs a <c>.bat</c> file: <c>cmd.exe /d /s /c ""&lt;bat&gt;" &lt;args&gt;"</c>. <c>/d</c> skips AutoRun commands,
    /// and with <c>/s</c> cmd takes off only the outer pair of quotes and runs the rest as it is. cmd doesn't read its
    /// command line by the rules .NET quotes arguments with (<c>\"</c> means nothing to it), so the line is built here
    /// and passed as it is (<see cref="ProcessStartSpec.CommandLine"/>).
    /// </summary>
    /// <exception cref="ArgumentException">An argument has a quote or a line break, which cmd can't be given safely.</exception>
    public static ProcessStartSpec BatchFile(string batchFile, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var inner = BatchCommand(batchFile, arguments);
        return new ProcessStartSpec(Cmd, ["/d", "/s", "/c", inner])
        {
            CommandLine = $"/d /s /c \"{inner}\"",
            WorkingDirectory = workingDirectory,
        };
    }

    /// <summary>The command cmd runs for a <c>.bat</c> file, as it would be typed: <c>"C:\UE 5\Build.bat" Target -Project="C:\My Game\Game.uproject"</c>.</summary>
    public static string BatchCommand(string batchFile, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(Check(batchFile)).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(QuoteForBatch(argument));
        }
        return builder.ToString();
    }

    /// <summary>
    /// One argument of a <c>.bat</c> file's command line. It's quoted when it's empty or has a space or a character cmd
    /// treats specially. An option with a value, such as <c>-project=C:\My Game\Game.uproject</c>, has only its value
    /// quoted (<c>-project="C:\My Game\Game.uproject"</c>), as Unreal's own tools write it.
    /// </summary>
    public static string QuoteForBatch(string argument)
    {
        Check(argument);
        if (!NeedsBatchQuotes(argument))
        {
            return argument;
        }
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        if (argument.StartsWith('-') && equals > 1 && !NeedsBatchQuotes(argument[..equals]))
        {
            return $"{argument[..(equals + 1)]}\"{argument[(equals + 1)..]}\"";
        }
        return $"\"{argument}\"";
    }

    private static bool NeedsBatchQuotes(string text) =>
        text.Length == 0 || text.Any(c => char.IsWhiteSpace(c) || c is '&' or '|' or '<' or '>' or '^' or '(' or ')' or ',' or ';' or '=' or '!' or '%' or '\'' or '`');

    private static string Check(string text)
    {
        if (text.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException($"cmd.exe can't be given a quote or a line break in an argument: {text}", nameof(text));
        }
        return text;
    }

    /// <summary>Runs a <c>.sh</c> file through <c>/bin/bash</c>, so it doesn't need to be executable.</summary>
    public static ProcessStartSpec ShellScript(string script, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
        new(Bash, [script, .. arguments]) { WorkingDirectory = workingDirectory };

    /// <summary>
    /// The user's own command, through their shell, as a terminal would run it: <c>cmd.exe /d /s /c "&lt;command&gt;"</c>
    /// on Windows, <c>$SHELL -c &lt;command&gt;</c> (or <c>/bin/sh</c>) elsewhere.
    /// </summary>
    /// <exception cref="ArgumentException">On Windows, the command has a line break.</exception>
    public static ProcessStartSpec ShellCommand(string command, ToolOS os, string? shell, string? workingDirectory = null)
    {
        if (os == ToolOS.Windows)
        {
            if (command.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new ArgumentException("A command for cmd.exe has to be one line.", nameof(command));
            }
            return new ProcessStartSpec(Cmd, ["/d", "/s", "/c", command])
            {
                CommandLine = $"/d /s /c \"{command}\"",
                WorkingDirectory = workingDirectory,
            };
        }
        return new ProcessStartSpec(string.IsNullOrWhiteSpace(shell) ? DefaultShell : shell.Trim(), ["-c", command]) { WorkingDirectory = workingDirectory };
    }

    /// <summary>
    /// How a command reads for people, and for Claude: what cmd runs for a <c>.bat</c> file, the script itself for a
    /// <c>.sh</c> file, otherwise the program and its arguments, quoted where needed.
    /// </summary>
    public static string Display(ProcessStartSpec spec)
    {
        var name = Path.GetFileName(spec.FileName.Replace('\\', '/'));
        if (string.Equals(name, Cmd, StringComparison.OrdinalIgnoreCase) && spec.Arguments is ["/d", "/s", "/c", var inner])
        {
            return inner;
        }
        if (spec.FileName == Bash && spec.Arguments.Count > 0 && spec.Arguments[0].EndsWith(".sh", StringComparison.Ordinal))
        {
            return string.Join(' ', spec.Arguments.Select(QuoteForShell));
        }
        if (spec.Arguments is ["-c", var command] && (spec.FileName == DefaultShell || name is "bash" or "zsh" or "sh" or "fish"))
        {
            return command;
        }
        var windows = spec.FileName.Contains('\\', StringComparison.Ordinal) || spec.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        Func<string, string> quote = windows ? QuoteForWindows : QuoteForShell;
        return string.Join(' ', new[] { spec.FileName }.Concat(spec.Arguments).Select(quote));
    }

    /// <summary>A POSIX shell word: as it is when safe, else in single quotes.</summary>
    public static string QuoteForShell(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or '=' or ':' or '+' or ',' or '@' or '%'))
        {
            return argument;
        }
        return $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    /// <summary>
    /// Quotes one argument of a Windows command line so <c>CommandLineToArgvW</c> (and the C runtime, and .NET) read it
    /// back as it was: in quotes when it's empty or has spaces or quotes, with a quote escaped and the backslashes before
    /// a quote doubled. A folder such as <c>D:\</c> needs that: <c>"D:\"</c> would read as <c>D:"</c>.
    /// </summary>
    public static string QuoteForWindows(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }
        var quoted = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes before a quote are escaped along with it; elsewhere they're literal.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
