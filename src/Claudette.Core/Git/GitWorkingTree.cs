using System.ComponentModel;
using System.Globalization;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;

namespace Claudette.Core.Git;

public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Untracked,
}

/// <summary>
/// A file that differs between HEAD and the working tree. Paths are absolute. The line counts are null for binary
/// files, and for untracked files too large to count.
/// </summary>
/// <param name="OldPath">The path in HEAD, for <see cref="GitChangeKind.Renamed"/>.</param>
public sealed record GitChange(string Path, GitChangeKind Kind, string? OldPath, int? Added, int? Removed);

/// <summary>
/// The "working tree vs HEAD" view of a repository (DESIGN.md §8), which also shows changes made by Bash commands or by
/// the user. Runs <c>git</c> from <c>PATH</c>. Every call has a timeout, and returns null or empty instead of throwing
/// when git is missing, times out, or the folder isn't in a repository.
/// </summary>
/// <param name="environment">
/// The user environment git runs with, and whose <c>PATH</c> it's found on (DESIGN.md §13), so git and its helpers
/// (Git LFS, credential helpers) are the ones a terminal would use. Null: Claudette's own.
/// </param>
public sealed class GitWorkingTree(IProcessLauncher launcher, TimeProvider timeProvider, string gitExecutable = "git", UserEnvironment? environment = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Untracked files larger than this aren't read to count their lines.</summary>
    public long MaxCountBytes { get; init; } = TextFiles.DefaultMaxBytes;

    /// <summary>The top folder of the repository containing <paramref name="folder"/>, or null.</summary>
    public async Task<string?> GetRepositoryRootAsync(string folder, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(folder, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            return null;
        }
        var root = result.StandardOutput.Split('\n', 2)[0].TrimEnd('\r');
        try
        {
            return root.Length == 0 ? null : Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The repository's top folder as git reports it, to run git from, and as <paramref name="folder"/> spells it, to put
    /// the paths git lists under. Git resolves symlinks, so on macOS the top of a folder under <c>/var</c> is under
    /// <c>/private/var</c>, and paths under it wouldn't match the same files as Claude and the tab name them. When the
    /// folder's path doesn't end in its path from the top, as when the folder is a symlink inside the repository, both
    /// are git's. Null when the folder isn't in a repository.
    /// </summary>
    private async Task<(string Root, string PathRoot)?> GetRootsAsync(string folder, CancellationToken cancellationToken)
    {
        var result = await RunAsync(folder, ["rev-parse", "--show-toplevel", "--show-prefix"], cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            return null;
        }
        var lines = result.StandardOutput.Split('\n', 3);
        var top = lines[0].TrimEnd('\r');
        var prefix = lines.Length > 1 ? lines[1].TrimEnd('\r') : "";
        try
        {
            if (top.Length == 0)
            {
                return null;
            }
            var root = Path.GetFullPath(top);
            var pathRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            foreach (var name in prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                if (!ChangedFiles.PlatformPathComparer.Equals(Path.GetFileName(pathRoot), name) || Path.GetDirectoryName(pathRoot) is not { } parent)
                {
                    return (root, root);
                }
                pathRoot = parent;
            }
            return (root, pathRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every file that differs from HEAD, including untracked ones, across the whole repository. In a repository with no
    /// commits yet, everything is added. Paths are spelled the way <paramref name="folder"/> is where that can be told,
    /// so they match Claude's changes to the same files (see <see cref="GetRootsAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<GitChange>> GetChangesAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (await GetRootsAsync(folder, cancellationToken).ConfigureAwait(false) is not (var root, var pathRoot))
        {
            return [];
        }
        // Run from the top: status paths are relative to it anyway, and then diff's are too, whatever diff.relative says.
        var hasHead = await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false) is { ExitCode: 0 };
        var statusTask = RunAsync(root, ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        var numstatTask = hasHead
            ? RunAsync(root, ["--no-optional-locks", "diff", "--numstat", "-z", "-M", "--no-ext-diff", "--no-textconv", "--no-color", "HEAD", "--"], cancellationToken)
            : Task.FromResult<ProcessResult?>(null);
        var status = await statusTask.ConfigureAwait(false);
        var numstat = await numstatTask.ConfigureAwait(false);
        if (status is not { ExitCode: 0 })
        {
            return [];
        }
        var counts = numstat is { ExitCode: 0 } ? ParseNumstat(numstat.StandardOutput) : new Dictionary<string, (int?, int?)>(StringComparer.Ordinal);

        var changes = new List<GitChange>();
        foreach (var (x, y, path, originalPath) in ParseStatus(status.StandardOutput))
        {
            var kind = Classify(x, y, originalPath is not null, hasHead);
            if (kind is null)
            {
                continue;
            }
            var reportedPath = kind == GitChangeKind.Deleted && originalPath is not null ? originalPath : path;
            var absolute = Absolute(pathRoot, reportedPath);
            (int? Added, int? Removed) lines = kind is GitChangeKind.Untracked || (kind is GitChangeKind.Added && !hasHead)
                ? CountAllAdded(absolute)
                : counts.TryGetValue(reportedPath, out var counted) ? counted : (null, null);
            var oldPath = kind == GitChangeKind.Renamed && originalPath is not null ? Absolute(pathRoot, originalPath) : null;
            changes.Add(new GitChange(absolute, kind.Value, oldPath, lines.Added, lines.Removed));
        }
        return changes;
    }

    /// <summary>
    /// The file's content in HEAD, or null when it isn't in HEAD (or there's no HEAD). Git's output arrives line by line,
    /// so line endings come back as <c>\n</c> and non-empty text always ends with one; <see cref="LineDiff"/> ignores both.
    /// </summary>
    public async Task<string?> GetHeadContentAsync(string folder, string absolutePath, CancellationToken cancellationToken = default)
    {
        if (await GetRepositoryRootAsync(folder, cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return null;
        }
        // Relative to the top (as git reports it) when possible; otherwise relative to the folder, which git resolves
        // itself. That covers a path spelled through a symlink, such as /var and /private/var on macOS.
        string revision;
        if (Inside(Path.GetRelativePath(root, absolutePath)) is { } fromRoot)
        {
            revision = "HEAD:" + fromRoot;
        }
        else if (Relative(Path.GetRelativePath(folder, absolutePath)) is { } fromFolder)
        {
            revision = fromFolder.StartsWith("../", StringComparison.Ordinal) ? "HEAD:" + fromFolder : "HEAD:./" + fromFolder;
        }
        else
        {
            return null;
        }

        var result = await RunAsync(folder, ["show", "--no-textconv", revision], cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } ? result.StandardOutput.Replace(Environment.NewLine, "\n", StringComparison.Ordinal) : null;
    }

    /// <summary>
    /// The files under <paramref name="folder"/> that git doesn't ignore, tracked or not, relative to it with forward
    /// slashes, for the composer's <c>@</c> autocomplete (DESIGN.md §5). Null when the folder isn't in a repository.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListFilesAsync(string folder, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(folder, ["--no-optional-locks", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            return null;
        }
        return TrimOutput(result.StandardOutput).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Whether anything differs from HEAD, including untracked files. Null when it can't be told.</summary>
    public async Task<bool?> HasUncommittedChangesAsync(string folder, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(folder, ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=normal"], cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } ? TrimOutput(result.StandardOutput).Length > 0 : null;
    }

    internal Task<ProcessResult?> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(workingDirectory, arguments, Timeout, cancellationToken);

    /// <param name="timeout">How long git may take, for a command that can take longer than most (deleting a worktree).</param>
    internal async Task<ProcessResult?> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
            {
                return null;
            }
            var spec = new ProcessStartSpec(gitExecutable, ["-c", "core.quotepath=false", .. arguments]) { WorkingDirectory = workingDirectory };
            if (environment is not null)
            {
                spec = await environment.ApplyAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            return await ProcessRunner.RunAsync(launcher, spec, timeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What an entry of <c>git status --porcelain=v1</c> means relative to HEAD; null for nothing (added and then
    /// deleted again, or ignored). X is the index column and Y the working tree column.
    /// </summary>
    private static GitChangeKind? Classify(char x, char y, bool hasOriginalPath, bool hasHead)
    {
        if (x == '?' && y == '?')
        {
            return GitChangeKind.Untracked;
        }
        if (x == '!')
        {
            return null;
        }
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
        {
            return GitChangeKind.Modified; // A merge conflict.
        }
        if (!hasHead)
        {
            return y == 'D' ? null : GitChangeKind.Added;
        }
        if (x == 'R' || y == 'R')
        {
            return y == 'D' ? GitChangeKind.Deleted : hasOriginalPath ? GitChangeKind.Renamed : GitChangeKind.Added;
        }
        if (x is 'A' or 'C' || y is 'A' or 'C')
        {
            return y == 'D' ? null : GitChangeKind.Added;
        }
        if (x == 'D' || y == 'D')
        {
            return GitChangeKind.Deleted;
        }
        return GitChangeKind.Modified;
    }

    /// <summary>
    /// Reads <c>git status --porcelain=v1 -z</c>: <c>XY path\0</c>, with the original path in the next field for a rename
    /// or copy.
    /// </summary>
    private static IEnumerable<(char X, char Y, string Path, string? OriginalPath)> ParseStatus(string output)
    {
        var fields = TrimOutput(output).Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length < 4 || field[2] != ' ')
            {
                continue;
            }
            char x = field[0], y = field[1];
            string? original = null;
            if ((x is 'R' or 'C' || y is 'R' or 'C') && i + 1 < fields.Length)
            {
                original = fields[++i];
            }
            yield return (x, y, field[3..], original);
        }
    }

    /// <summary>
    /// Reads <c>git diff --numstat -z</c>: <c>added\tremoved\tpath\0</c>, or for a rename
    /// <c>added\tremoved\t\0old\0new\0</c>. Binary files show <c>-</c> for both counts.
    /// </summary>
    private static Dictionary<string, (int?, int?)> ParseNumstat(string output)
    {
        var counts = new Dictionary<string, (int?, int?)>(StringComparer.Ordinal);
        var fields = TrimOutput(output).Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var parts = fields[i].Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }
            var path = parts[2];
            if (path.Length == 0)
            {
                if (i + 2 >= fields.Length)
                {
                    break;
                }
                path = fields[i + 2];
                i += 2;
            }
            counts[path] = (Number(parts[0]), Number(parts[1]));
        }
        return counts;

        static int? Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private (int?, int?) CountAllAdded(string path)
    {
        if (Path.EndsInDirectorySeparator(path))
        {
            return (null, null); // A nested repository.
        }
        var read = TextFiles.Read(path, MaxCountBytes);
        return read.Kind == TextFileKind.Text ? (LineDiff.CountLines(read.Text), 0) : (null, null);
    }

    /// <summary>ProcessRunner ends the output with a newline of its own.</summary>
    private static string TrimOutput(string output) =>
        output.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? output[..^Environment.NewLine.Length] : output;

    private static string Absolute(string root, string repoRelative) =>
        Path.GetFullPath(Path.Combine(root, repoRelative.Replace('/', Path.DirectorySeparatorChar)));

    private static string? Inside(string relative) =>
        Relative(relative) is { } path && path != ".." && !path.StartsWith("../", StringComparison.Ordinal) ? path : null;

    private static string? Relative(string relative) =>
        relative == "." || Path.IsPathRooted(relative) ? null : relative.Replace(Path.DirectorySeparatorChar, '/');
}
