using System.Security.Cryptography;
using System.Text;
using Claudette.Core.Git;
using Claudette.Core.Settings;

namespace Claudette.Core.ScratchPads;

/// <summary>
/// Which scratch pad a folder's tabs share (DESIGN.md §18, "Scratch pad"): one per project. A folder in a git repository
/// with a remote is known by the remote and the folder's path inside the repository, as a session's project identity is
/// (DESIGN.md §9), so every branch, worktree and clone of it shares the pad, on any machine. Without a remote the pad is
/// this machine's: a repository's is known by its git folder and the path inside it, so its worktrees still share it,
/// and any other folder's by its path. Read from files only; no git process.
/// </summary>
/// <param name="Id">The pad's file name: a hash, so the library's file names don't name remotes or folders.</param>
/// <param name="Remote">The repository's remote in its normal form; null for a pad that stays on this machine.</param>
/// <param name="PathInRepo">The folder inside the repository, with forward slashes; <c>""</c> for its top or outside one.</param>
/// <param name="IsInRepository">The folder is in a git repository: why a pad that isn't shared isn't.</param>
public sealed record ScratchPadProject(string Id, string? Remote, string PathInRepo, bool IsInRepository)
{
    /// <summary>The pad syncs through the session library.</summary>
    public bool IsShared => Remote is not null;

    public static ScratchPadProject For(string folder)
    {
        if (GitDirectory.Find(folder) is { } repository)
        {
            try
            {
                var pathInRepo = ProjectIdentity.RelativePath(repository.WorkTree, folder);
                // Folders are compared without case, as git on Windows and macOS does; a remote already is.
                var inRepo = pathInRepo.ToLowerInvariant();
                return repository.ReadRemoteUrl() is { } url && ProjectIdentity.NormalizeRemote(url) is { Length: > 0 } remote
                    ? new ScratchPadProject(Hash($"git\n{remote}\n{inRepo}"), remote, pathInRepo, IsInRepository: true)
                    : new ScratchPadProject(Hash($"repository\n{PathKey(repository.CommonDir)}\n{inRepo}"), null, pathInRepo, IsInRepository: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Its files can't be read: the folder's own pad.
            }
        }
        return new ScratchPadProject(Hash($"folder\n{PathKey(folder)}"), null, "", IsInRepository: false);
    }

    /// <summary>A folder's path as one key, however it was written: Windows' paths are compared without case.</summary>
    private static string PathKey(string folder)
    {
        var normalized = FolderHistory.Normalize(folder).Replace('\\', '/');
        return OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
