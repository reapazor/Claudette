namespace Claudette.Core.Git;

/// <summary>
/// Which project a session belongs to, in a form that survives moving between machines (DESIGN.md §9, "Restoring on
/// another machine"): folder paths differ (<c>D:\Repos\api</c> vs <c>/Users/me/src/api</c>), so a session records the
/// git remote, branch, commit and the path inside the repository instead. Read from files only; no git process.
/// </summary>
/// <param name="RemoteUrl">The URL of <c>origin</c>, else of the first remote; null when there's no remote.</param>
/// <param name="Branch">The checked-out branch; null on a detached HEAD.</param>
/// <param name="Commit">The full commit id HEAD points at; null when it can't be read (for example a new repository).</param>
/// <param name="PathInRepo">The folder relative to the repository root, with forward slashes; <c>""</c> for the root.</param>
public sealed record ProjectIdentity(string? RemoteUrl, string? Branch, string? Commit, string PathInRepo)
{
    /// <summary>The identity of the repository containing <paramref name="folder"/>, or null outside a repository.</summary>
    public static ProjectIdentity? Read(string folder)
    {
        try
        {
            if (GitDirectory.Find(folder) is not { } repository)
            {
                return null;
            }
            var (branch, commit) = repository.ReadHead();
            return new ProjectIdentity(repository.ReadRemoteUrl(), branch, commit, RelativePath(repository.WorkTree, folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A remote URL reduced to <c>host/path</c> so that the SSH, HTTPS and scp-style forms of the same repository
    /// compare equal: <c>git@github.com:Owner/Repo.git</c>, <c>https://user:token@github.com/owner/repo.git</c>,
    /// <c>ssh://git@github.com/owner/repo</c> and <c>https://github.com/owner/repo/</c> all become
    /// <c>github.com/owner/repo</c>. Credentials, ports, a trailing <c>.git</c> and slashes are dropped, and the result
    /// is lowercase. <c>file://</c> URLs and local paths become the lowercase path with forward slashes.
    /// </summary>
    public static string NormalizeRemote(string url)
    {
        var text = url.Trim();
        if (text.Length == 0)
        {
            return "";
        }

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var rest = text[(scheme + 3)..];
            if (text[..scheme].Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePath(rest);
            }
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var authority = slash < 0 ? rest : rest[..slash];
            return Join(Host(authority), slash < 0 ? "" : rest[(slash + 1)..]);
        }

        if (IsLocalPath(text))
        {
            return NormalizePath(text);
        }

        // scp-like syntax: [user@]host:path, where the colon comes before any slash.
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var firstSlash = text.IndexOf('/', StringComparison.Ordinal);
        if (colon > 0 && (firstSlash < 0 || colon < firstSlash))
        {
            return Join(Host(text[..colon]), text[(colon + 1)..]);
        }
        return NormalizePath(text);
    }

    /// <summary>
    /// Finds this machine's folder for a recorded project. Each candidate (recent folders, which may be subfolders of a
    /// repository) is read; when its normalized remote matches, <c>&lt;its repository root&gt;/&lt;PathInRepo&gt;</c> is
    /// returned if that folder exists. A candidate on the recorded branch is preferred over an earlier one that isn't.
    /// Null when nothing matches, or when the recorded project has no remote.
    /// </summary>
    public static string? FindMatchingFolder(ProjectIdentity recorded, IEnumerable<string> candidateRepoFolders)
    {
        if (recorded.RemoteUrl is not { Length: > 0 } remote)
        {
            return null;
        }
        var wanted = NormalizeRemote(remote);
        var pathInRepo = (recorded.PathInRepo ?? "").Replace('\\', '/').Trim('/');
        string? fallback = null;
        foreach (var candidate in candidateRepoFolders)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)
                    || GitDirectory.Find(candidate) is not { } repository
                    || repository.ReadRemoteUrl() is not { } url
                    || NormalizeRemote(url) != wanted)
                {
                    continue;
                }
                var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                    Path.Combine(repository.WorkTree, pathInRepo.Replace('/', Path.DirectorySeparatorChar))));
                // A path from another machine mustn't lead outside the repository.
                var inside = Path.GetRelativePath(repository.WorkTree, target);
                if (inside == ".." || inside.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(inside)
                    || !Directory.Exists(target))
                {
                    continue;
                }
                if (recorded.Branch is not null && repository.ReadHead().Branch == recorded.Branch)
                {
                    return target;
                }
                fallback ??= target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Skip a candidate that can't be read.
            }
        }
        return fallback;
    }

    /// <summary>How the code in <paramref name="local"/> differs from what the other machine had.</summary>
    /// <param name="recordedDirty">The other machine had uncommitted changes.</param>
    /// <param name="local">This machine's folder; null when it isn't in a repository.</param>
    public static CodeDifference Compare(ProjectIdentity recorded, bool recordedDirty, ProjectIdentity? local) => new(
        BranchDiffers: !string.Equals(recorded.Branch, local?.Branch, StringComparison.Ordinal),
        CommitDiffers: !string.Equals(recorded.Commit, local?.Commit, StringComparison.OrdinalIgnoreCase),
        HadUncommittedChanges: recordedDirty);

    /// <summary>The first 7 characters of a commit id, as git shows it.</summary>
    public static string ShortCommit(string commit) => commit.Length > 7 ? commit[..7] : commit;

    private static string RelativePath(string workTree, string folder)
    {
        var relative = Path.GetRelativePath(workTree, Path.GetFullPath(folder));
        return relative == "." ? "" : relative.Replace('\\', '/').Trim('/');
    }

    /// <summary>The host of a URL authority, without credentials or a port.</summary>
    private static string Host(string authority)
    {
        var at = authority.LastIndexOf('@');
        var host = at >= 0 ? authority[(at + 1)..] : authority;
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? host[..(close + 1)] : host;
        }
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? host[..colon] : host;
    }

    private static string Join(string host, string path)
    {
        var trimmed = StripGitSuffix(path.Replace('\\', '/').Trim('/'));
        var lowerHost = host.ToLowerInvariant();
        return trimmed.Length == 0 ? lowerHost : $"{lowerHost}/{trimmed.ToLowerInvariant()}";
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        // file:///C:/repos/x has a slash before the drive letter.
        if (normalized.Length >= 3 && normalized[0] == '/' && char.IsAsciiLetter(normalized[1]) && normalized[2] == ':')
        {
            normalized = normalized[1..];
        }
        return StripGitSuffix(normalized.TrimEnd('/')).ToLowerInvariant();
    }

    private static string StripGitSuffix(string path)
    {
        var stripped = path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        return stripped.TrimEnd('/');
    }

    private static bool IsLocalPath(string text) =>
        text[0] is '/' or '\\' or '.' or '~'
        || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text.Length == 2 || text[2] is '/' or '\\'));
}

/// <summary>
/// How this machine's code differs from what the other machine had when it last used a session (DESIGN.md §9,
/// "Restoring on another machine", step 2). The library moves the conversation, not the code.
/// </summary>
public sealed record CodeDifference(bool BranchDiffers, bool CommitDiffers, bool HadUncommittedChanges)
{
    /// <summary>True when the user should be warned before resuming.</summary>
    public bool Any => BranchDiffers || CommitDiffers || HadUncommittedChanges;

    /// <summary>
    /// The warning, for example: <i>This session was last used on DESKTOP-01 on branch `feature/auth` at `a1b2c3d`.
    /// This folder is on `main`. Claude's earlier file changes may not be here.</i> Empty when nothing differs.
    /// </summary>
    public string Describe(string machine, ProjectIdentity recorded, ProjectIdentity? local)
    {
        if (!Any)
        {
            return "";
        }
        var sentences = new List<string>(4);

        var where = recorded.Branch is { } branch ? $" on branch `{branch}`" : "";
        var at = recorded.Commit is { } commit ? $" at `{ProjectIdentity.ShortCommit(commit)}`" : "";
        sentences.Add($"This session was last used on {machine}{where}{at}.");

        if (local is null)
        {
            if (BranchDiffers || CommitDiffers)
            {
                sentences.Add("This folder isn't in a git repository.");
            }
        }
        else if (BranchDiffers)
        {
            sentences.Add(local switch
            {
                { Branch: { } localBranch } => $"This folder is on `{localBranch}`.",
                { Commit: { } localCommit } => $"This folder is on a detached HEAD at `{ProjectIdentity.ShortCommit(localCommit)}`.",
                _ => "This folder is on a different branch.",
            });
        }
        else if (CommitDiffers)
        {
            sentences.Add(local.Commit is { } localCommit
                ? $"This folder is at `{ProjectIdentity.ShortCommit(localCommit)}`."
                : "This folder has no commits yet.");
        }

        if (HadUncommittedChanges)
        {
            sentences.Add($"{machine} had uncommitted changes.");
        }
        sentences.Add("Claude's earlier file changes may not be here.");
        return string.Join(" ", sentences);
    }
}
