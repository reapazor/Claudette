using System.Text;

namespace Claudette.Core.Git;

/// <summary>
/// A repository found by reading files only, the way <see cref="GitInfo"/> does: <c>.git</c> is a folder, or a file
/// holding <c>gitdir: &lt;path&gt;</c> for worktrees and submodules. A linked worktree's own git folder holds HEAD,
/// while the config, branches and <c>packed-refs</c> live in the common folder named by its <c>commondir</c> file.
/// </summary>
/// <param name="WorkTree">The repository's top folder (the one holding <c>.git</c>).</param>
/// <param name="GitDir">The git folder for this work tree.</param>
/// <param name="CommonDir">The folder shared by all worktrees: the same as <paramref name="GitDir"/> for a normal clone.</param>
internal sealed record GitDirectory(string WorkTree, string GitDir, string CommonDir)
{
    private const string GitDirPrefix = "gitdir:";

    /// <summary>The repository containing <paramref name="folder"/>, or null when there isn't one or it can't be read.</summary>
    public static GitDirectory? Find(string folder)
    {
        try
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(folder)); directory is not null; directory = directory.Parent)
            {
                var dotGit = Path.Combine(directory.FullName, ".git");
                string? gitDir = null;
                if (Directory.Exists(dotGit))
                {
                    gitDir = dotGit;
                }
                else if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var pointer && pointer.StartsWith(GitDirPrefix, StringComparison.Ordinal))
                {
                    gitDir = Resolve(directory.FullName, pointer[GitDirPrefix.Length..].Trim());
                }
                if (gitDir is null)
                {
                    continue;
                }
                if (!File.Exists(Path.Combine(gitDir, "HEAD")))
                {
                    return null;
                }
                var commonDir = gitDir;
                var commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile) && File.ReadAllText(commonFile).Trim() is { Length: > 0 } common)
                {
                    commonDir = Resolve(gitDir, common);
                }
                return new GitDirectory(directory.FullName, gitDir, commonDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable: treat as not a repository.
        }
        return null;
    }

    /// <summary>The checked-out branch (null when detached) and the commit HEAD points at (null when unknown).</summary>
    public (string? Branch, string? Commit) ReadHead()
    {
        var head = ReadFile(Path.Combine(GitDir, "HEAD"));
        if (head is null)
        {
            return (null, null);
        }
        if (head.StartsWith("ref:", StringComparison.Ordinal))
        {
            var refName = head["ref:".Length..].Trim();
            const string heads = "refs/heads/";
            var branch = refName.StartsWith(heads, StringComparison.Ordinal) ? refName[heads.Length..] : refName;
            return (branch, ResolveRef(refName, depth: 0));
        }
        return (null, IsObjectId(head) ? head.ToLowerInvariant() : null);
    }

    /// <summary>
    /// What HEAD names, read from it alone without resolving refs: the branch, or the first 7 characters of the commit
    /// when detached. Null when HEAD can't be read.
    /// </summary>
    public string? ReadHeadName()
    {
        var head = ReadFile(Path.Combine(GitDir, "HEAD"));
        if (head is null)
        {
            return null;
        }
        if (head.StartsWith("ref:", StringComparison.Ordinal))
        {
            var refName = head["ref:".Length..].Trim();
            const string heads = "refs/heads/";
            return refName.StartsWith(heads, StringComparison.Ordinal) ? refName[heads.Length..] : refName;
        }
        return head.Length >= 7 ? head[..7] : null;
    }

    /// <summary>The fetch URL of <c>origin</c>, else of the first remote in the config, else null.</summary>
    public string? ReadRemoteUrl()
    {
        var config = Path.Combine(CommonDir, "config");
        if (!File.Exists(config))
        {
            return null;
        }
        string? section = null;
        string? first = null;
        foreach (var raw in File.ReadLines(config))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var close = line.IndexOf(']', StringComparison.Ordinal);
                if (close < 0)
                {
                    section = null;
                    continue;
                }
                section = RemoteName(line[1..close].Trim());
                line = line[(close + 1)..].Trim();
            }
            if (section is null || line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 || !line[..equals].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var url = ConfigValue(line[(equals + 1)..]);
            if (url.Length == 0)
            {
                continue;
            }
            if (section == "origin")
            {
                return url;
            }
            first ??= url;
        }
        return first;
    }

    private string? ResolveRef(string refName, int depth)
    {
        if (depth > 5)
        {
            return null;
        }
        var relative = refName.Replace('/', Path.DirectorySeparatorChar);
        // A worktree's own folder holds per-worktree refs; branches are in the common folder.
        var roots = GitDir == CommonDir ? new[] { GitDir } : [GitDir, CommonDir];
        foreach (var root in roots)
        {
            if (ReadFile(Path.Combine(root, relative)) is { } content)
            {
                if (content.StartsWith("ref:", StringComparison.Ordinal))
                {
                    return ResolveRef(content["ref:".Length..].Trim(), depth + 1);
                }
                if (IsObjectId(content))
                {
                    return content.ToLowerInvariant();
                }
            }
        }

        var packed = Path.Combine(CommonDir, "packed-refs");
        if (!File.Exists(packed))
        {
            return null;
        }
        foreach (var line in File.ReadLines(packed))
        {
            if (line.Length == 0 || line[0] is '#' or '^')
            {
                continue;
            }
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && line[(space + 1)..].Trim() == refName && IsObjectId(line[..space]))
            {
                return line[..space].ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary><c>remote "origin"</c> (or the old <c>remote.origin</c> form) to <c>origin</c>; null for other sections.</summary>
    private static string? RemoteName(string header)
    {
        if (header.StartsWith("remote.", StringComparison.OrdinalIgnoreCase))
        {
            return header["remote.".Length..];
        }
        if (header.Length > "remote".Length
            && header.StartsWith("remote", StringComparison.OrdinalIgnoreCase)
            && char.IsWhiteSpace(header["remote".Length]))
        {
            return header["remote".Length..].Trim().Trim('"');
        }
        return null;
    }

    /// <summary>A config value with quotes, escapes and trailing comments handled.</summary>
    private static string ConfigValue(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var quoted = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[++i];
                builder.Append(next switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => next });
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '#' or ';')
            {
                break;
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString().Trim();
    }

    private static string Resolve(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    private static string? ReadFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    /// <summary>A full SHA-1 or SHA-256 object id.</summary>
    private static bool IsObjectId(string text) =>
        text.Length is 40 or 64 && text.All(char.IsAsciiHexDigit);
}
