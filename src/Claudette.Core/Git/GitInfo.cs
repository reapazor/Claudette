namespace Claudette.Core.Git;

/// <summary>Cheap git facts read straight from the <c>.git</c> folder, without running git.</summary>
public static class GitInfo
{
    /// <summary>
    /// The current branch of the repository containing <paramref name="folder"/>, a short commit id when detached, or
    /// null when it isn't in a repository. Works for worktrees, where <c>.git</c> is a file pointing elsewhere.
    /// </summary>
    public static string? TryGetBranch(string folder)
    {
        try
        {
            for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
            {
                var dotGit = Path.Combine(directory.FullName, ".git");
                string? gitDirectory = null;
                if (Directory.Exists(dotGit))
                {
                    gitDirectory = dotGit;
                }
                else if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var target = pointer["gitdir:".Length..].Trim();
                    gitDirectory = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(directory.FullName, target));
                }
                if (gitDirectory is null)
                {
                    continue;
                }
                var head = Path.Combine(gitDirectory, "HEAD");
                if (!File.Exists(head))
                {
                    return null;
                }
                var content = File.ReadAllText(head).Trim();
                const string refPrefix = "ref: refs/heads/";
                return content.StartsWith(refPrefix, StringComparison.Ordinal)
                    ? content[refPrefix.Length..]
                    : content.Length >= 7 ? content[..7] : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable: treat as not a repository.
        }
        return null;
    }
}
