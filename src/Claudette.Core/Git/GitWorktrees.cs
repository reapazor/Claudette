namespace Claudette.Core.Git;

/// <summary>A git worktree, as <c>git worktree list --porcelain</c> lists it.</summary>
/// <param name="Branch">The branch checked out, without <c>refs/heads/</c>; null when detached.</param>
/// <param name="IsMain">The repository's main checkout, which is never removed.</param>
public sealed record GitWorktree(string Path, string? Branch, bool IsLocked, bool IsMain);

/// <summary>What removing a worktree would discard (DESIGN.md §4, "Worktree tabs").</summary>
/// <param name="HasChanges">Changed or untracked files.</param>
/// <param name="OwnCommits">Commits on no other branch, local or remote.</param>
public sealed record GitWorktreeWork(bool HasChanges, int OwnCommits)
{
    public bool IsEmpty => !HasChanges && OwnCommits == 0;
}

/// <summary>
/// The worktrees of worktree tabs (DESIGN.md §4, "Worktree tabs"): Claude Code creates them with <c>--worktree</c> and,
/// in <c>-p</c> mode, leaves them behind, locked; Claudette lists, inspects and removes them. Like
/// <see cref="GitWorkingTree"/>, every call returns null or false instead of throwing when git can't answer.
/// </summary>
public sealed class GitWorktrees(GitWorkingTree git)
{
    /// <summary>The prefix Claude Code gives the branch of a worktree it creates: <c>worktree-&lt;name&gt;</c>.</summary>
    public const string BranchPrefix = "worktree-";

    /// <summary>Where Claude Code creates a worktree, under the repository's top folder: <c>.claude/worktrees/&lt;name&gt;</c>.</summary>
    public static string PathFor(string repositoryRoot, string name) => System.IO.Path.Combine(repositoryRoot, ".claude", "worktrees", name);

    /// <summary>
    /// The main checkout of a worktree Claude Code made, from its path (<c>&lt;checkout&gt;/.claude/worktrees/&lt;name&gt;</c>),
    /// or null for any other folder.
    /// </summary>
    public static string? MainCheckoutOf(string folder)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder));
        var worktrees = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(folder));
        var claude = worktrees is null ? null : System.IO.Path.GetDirectoryName(worktrees);
        return name.Length > 0 && System.IO.Path.GetFileName(worktrees) == "worktrees" && System.IO.Path.GetFileName(claude) == ".claude"
            ? System.IO.Path.GetDirectoryName(claude)
            : null;
    }

    private static readonly string[] Adjectives =
        ["amber", "brisk", "calm", "clever", "cosmic", "daring", "eager", "gentle", "golden", "humble", "jolly", "keen",
         "lively", "lucky", "mellow", "nimble", "quiet", "rapid", "sunny", "swift", "tidy", "vivid", "witty", "zesty"];

    private static readonly string[] Nouns =
        ["badger", "beacon", "comet", "falcon", "fern", "harbor", "heron", "island", "lantern", "maple", "meadow", "otter",
         "pebble", "pine", "quartz", "raven", "river", "sparrow", "summit", "thistle", "tiger", "violet", "willow", "wren"];

    /// <summary>A name for a new worktree, such as <c>brisk-otter</c>, that <paramref name="taken"/> doesn't refuse.</summary>
    public static string NewName(Random random, Func<string, bool> taken)
    {
        var name = $"{Adjectives[random.Next(Adjectives.Length)]}-{Nouns[random.Next(Nouns.Length)]}";
        var candidate = name;
        for (var n = 2; taken(candidate); n++)
        {
            candidate = $"{name}-{n}";
        }
        return candidate;
    }

    /// <summary>Reads <c>git worktree list --porcelain -z</c>: records of NUL-ended lines, each record ending in an empty one.</summary>
    public static IReadOnlyList<GitWorktree> ParseList(string porcelain)
    {
        var worktrees = new List<GitWorktree>();
        string? path = null;
        string? branch = null;
        var locked = false;
        foreach (var line in porcelain.Split('\0'))
        {
            if (line.Length == 0)
            {
                if (path is not null)
                {
                    worktrees.Add(new GitWorktree(path, branch, locked, worktrees.Count == 0));
                }
                (path, branch, locked) = (null, null, false);
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = Normalize(line["worktree ".Length..]);
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var reference = line["branch ".Length..];
                branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                locked = true;
            }
        }
        if (path is not null)
        {
            worktrees.Add(new GitWorktree(path, branch, locked, worktrees.Count == 0));
        }
        return worktrees;
    }

    /// <summary>The worktrees of the repository <paramref name="folder"/> is in, the main checkout first; null when git can't tell.</summary>
    public async Task<IReadOnlyList<GitWorktree>?> ListAsync(string folder, CancellationToken cancellationToken = default)
    {
        var result = await git.RunAsync(folder, ["worktree", "list", "--porcelain", "-z"], cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } ? ParseList(result.StandardOutput) : null;
    }

    /// <summary>
    /// The worktree at <paramref name="path"/>, among those of the repository <paramref name="folder"/> is in; null when
    /// it isn't one, or git can't tell. The main checkout is never returned.
    /// </summary>
    public async Task<GitWorktree?> FindAsync(string folder, string path, CancellationToken cancellationToken = default)
    {
        var target = Normalize(path);
        return (await ListAsync(folder, cancellationToken).ConfigureAwait(false))?
            .FirstOrDefault(w => !w.IsMain && Diffs.ChangedFiles.PlatformPathComparer.Equals(w.Path, target));
    }

    /// <summary>What removing <paramref name="worktree"/> would discard; null when git can't tell, which counts as work.</summary>
    public async Task<GitWorktreeWork?> InspectAsync(GitWorktree worktree, CancellationToken cancellationToken = default)
    {
        if (await git.HasUncommittedChangesAsync(worktree.Path, cancellationToken).ConfigureAwait(false) is not { } changes)
        {
            return null;
        }
        // Commits reachable from this worktree's HEAD and from no other branch: what deleting its branch would lose.
        IReadOnlyList<string> arguments = worktree.Branch is { } branch
            ? ["rev-list", "--count", "HEAD", "--not", $"--exclude={branch}", "--branches", "--remotes"]
            : ["rev-list", "--count", "HEAD", "--not", "--branches", "--remotes"];
        var result = await git.RunAsync(worktree.Path, arguments, cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } && int.TryParse(result.StandardOutput.Trim(), out var own)
            ? new GitWorktreeWork(changes, own)
            : null;
    }

    /// <summary>
    /// Removes <paramref name="worktree"/>, run from <paramref name="mainFolder"/>: unlocks it (Claude Code leaves the
    /// lock it took), removes it, with <paramref name="discard"/> even with changes in it, and deletes its branch when
    /// Claude Code made it (<c>worktree-&lt;name&gt;</c>) and either it has no commits of its own or they're discarded too.
    /// Returns null when it's gone, else what git said.
    /// </summary>
    public async Task<string?> RemoveAsync(string mainFolder, GitWorktree worktree, bool discard, int ownCommits, CancellationToken cancellationToken = default)
    {
        if (worktree.IsMain)
        {
            return "The main checkout isn't removed.";
        }
        if (worktree.IsLocked)
        {
            await git.RunAsync(mainFolder, ["worktree", "unlock", worktree.Path], cancellationToken).ConfigureAwait(false);
        }
        var removed = await git.RunAsync(mainFolder, discard ? ["worktree", "remove", "--force", worktree.Path] : ["worktree", "remove", worktree.Path], cancellationToken)
            .ConfigureAwait(false);
        if (removed is not { ExitCode: 0 })
        {
            return removed is null ? "Git couldn't be run." : FirstLine(removed.StandardError) ?? $"git worktree remove failed ({removed.ExitCode}).";
        }
        if (worktree.Branch is { } branch && branch.StartsWith(BranchPrefix, StringComparison.Ordinal) && (discard || ownCommits == 0))
        {
            var deleted = await git.RunAsync(mainFolder, ["branch", "-D", branch], cancellationToken).ConfigureAwait(false);
            if (deleted is not { ExitCode: 0 })
            {
                return $"The worktree is gone, but its branch {branch} isn't: {FirstLine(deleted?.StandardError) ?? "git couldn't delete it."}";
            }
        }
        return null;
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
