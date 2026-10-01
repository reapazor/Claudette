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
            return GitDirectory.Find(folder)?.ReadHeadName();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable: treat as not a repository.
            return null;
        }
    }
}
