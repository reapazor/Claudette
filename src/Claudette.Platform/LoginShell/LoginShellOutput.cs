using System.Text;

namespace Claudette.Platform.LoginShell;

/// <summary>
/// The command a login shell runs to print its environment, and reading what it prints (DESIGN.md §13, "Login shell
/// environment"). Rc files often print things of their own, so the environment is fenced by a random marker, and
/// anything outside the fence is ignored.
/// </summary>
internal static class LoginShellOutput
{
    private enum Family
    {
        /// <summary>bash, zsh and the other POSIX shells: <c>-i -l -c</c>.</summary>
        Posix,

        /// <summary>fish: <c>-l -i -c</c>.</summary>
        Fish,

        /// <summary>tcsh and csh, which take <c>-l</c> only on its own: <c>-i -c</c>, which reads <c>~/.cshrc</c> or <c>~/.tcshrc</c>.</summary>
        Csh,
    }

    private static readonly Dictionary<string, Family> Shells = new(StringComparer.Ordinal)
    {
        ["bash"] = Family.Posix,
        ["zsh"] = Family.Posix,
        ["sh"] = Family.Posix,
        ["dash"] = Family.Posix,
        ["ksh"] = Family.Posix,
        ["ksh93"] = Family.Posix,
        ["mksh"] = Family.Posix,
        ["oksh"] = Family.Posix,
        ["yash"] = Family.Posix,
        ["fish"] = Family.Fish,
        ["tcsh"] = Family.Csh,
        ["csh"] = Family.Csh,
    };

    /// <summary>The shells Claudette can read, in words.</summary>
    public const string SupportedNames = "bash, zsh, fish, sh, dash, ksh, mksh, yash, tcsh and csh";

    /// <summary>
    /// The arguments that make <paramref name="shell"/> a login, interactive shell running <see cref="Command"/>, as
    /// VS Code does it. Null for a shell Claudette doesn't know how to ask, such as nushell, xonsh or PowerShell.
    /// </summary>
    public static IReadOnlyList<string>? Arguments(string shell, string marker) =>
        Shells.TryGetValue(Path.GetFileName(shell), out var family)
            ? family switch
            {
                Family.Fish => ["-l", "-i", "-c", Command(marker)],
                Family.Csh => ["-i", "-c", Command(marker)],
                _ => ["-i", "-l", "-c", Command(marker)],
            }
            : null;

    /// <summary>
    /// Prints the environment twice between markers: NUL-separated (<c>env -0</c>), which keeps values with line breaks
    /// whole, then one per line, for an <c>env</c> without <c>-0</c>. The same text works in POSIX shells, fish and csh:
    /// only <c>;</c>, and markers that <c>printf</c> prints as they are. <c>/usr/bin/env</c> is named in full so an alias
    /// or function called <c>env</c> doesn't matter.
    /// </summary>
    public static string Command(string marker) =>
        $"printf {marker}A; /usr/bin/env -0; printf {marker}B; /usr/bin/env; printf {marker}C; exit 0";

    /// <summary>
    /// The environment printed between the markers, or null when there's none. Anything before and after, such as a
    /// greeting from an rc file, is ignored. A carriage return in a value comes back as a line break: output is read a
    /// line at a time.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Parse(string output, string marker)
    {
        output = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var a = output.IndexOf(marker + "A", StringComparison.Ordinal);
        if (a < 0)
        {
            return null;
        }
        var aEnd = a + marker.Length + 1;
        var b = output.IndexOf(marker + "B", aEnd, StringComparison.Ordinal);
        if (b < 0)
        {
            return null;
        }
        if (ParseNulSeparated(output[aEnd..b]) is { Count: > 0 } separated)
        {
            return separated;
        }
        var bEnd = b + marker.Length + 1;
        var c = output.IndexOf(marker + "C", bEnd, StringComparison.Ordinal);
        if (c < 0)
        {
            return null;
        }
        return ParseLines(output[bEnd..c]) is { Count: > 0 } lines ? lines : null;
    }

    /// <summary><c>NAME=value</c> entries separated by NUL. Values may hold <c>=</c> and line breaks.</summary>
    internal static Dictionary<string, string> ParseNulSeparated(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!text.Contains('\0', StringComparison.Ordinal))
        {
            return result;
        }
        foreach (var entry in text.Split('\0'))
        {
            var equals = entry.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && !entry.AsSpan(0, equals).ContainsAny('\n', '\r'))
            {
                result[entry[..equals]] = entry[(equals + 1)..];
            }
        }
        return result;
    }

    /// <summary>
    /// <c>NAME=value</c> lines, the fallback. A line that doesn't start with a name and <c>=</c> continues the value
    /// before it, which keeps most multi-line values, such as exported bash functions, whole.
    /// </summary>
    internal static Dictionary<string, string> ParseLines(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }
        string? key = null;
        var value = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && IsName(line.AsSpan(0, equals)))
            {
                if (key is not null)
                {
                    result[key] = value.ToString();
                }
                key = line[..equals];
                value.Clear().Append(line, equals + 1, line.Length - equals - 1);
            }
            else if (key is not null)
            {
                value.Append('\n').Append(line);
            }
        }
        if (key is not null)
        {
            result[key] = value.ToString();
        }
        return result;
    }

    /// <summary>A variable name as shells export them, allowing the <c>BASH_FUNC_name%%</c> of an exported function.</summary>
    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '%' or '.' or '-'))
            {
                return false;
            }
        }
        return true;
    }
}
