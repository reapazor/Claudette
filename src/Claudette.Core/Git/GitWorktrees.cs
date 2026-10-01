namespace Claudette.Core.Git;

/// <summary>A git worktree, as <c>git worktree list --porcelain</c> lists it.</summary>
/// <param name="Branch">The branch checked out, without <c>refs/heads/</c>; null when detached.</param>
/// <param name="IsMain">The repository's main checkout, which is never removed.</param>
public sealed record GitWorktree(string Path, string? Branch, bool IsLocked, bool IsMain);

/// <summary>What removing a worktree would discard (DESIGN.md §4, "Worktree tabs").</summary>
/// <param name="HasChanges">Changed or untracked files.</param>
/// <param name="OwnCommits">Commits on no other branch, local or remote.</param>
/// <param name="HasIgnoredFiles">
/// Files git ignores, such as build output, a local <c>.env</c> or Claude Code's <c>.claude/settings.local.json</c>:
/// not work git keeps, but they go with the worktree.
/// </param>
public sealed record GitWorktreeWork(bool HasChanges, int OwnCommits, bool HasIgnoredFiles = false)
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

    /// <summary>
    /// How long <c>git worktree remove</c> may take: it deletes the whole folder, ignored build output and
    /// dependencies included, which can be far more than git's usual few seconds.
    /// </summary>
    public static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(10);

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

    /// <summary>
    /// Reads <c>git worktree list --porcelain -z</c>: records of NUL-ended lines, each record ending in an empty one.
    /// Without <c>-z</c> (git before 2.36), the lines end in newlines instead.
    /// </summary>
    public static IReadOnlyList<GitWorktree> ParseList(string porcelain)
    {
        if (!porcelain.Contains('\0', StringComparison.Ordinal))
        {
            porcelain = porcelain.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\0');
        }
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
        if (result is { ExitCode: 0 })
        {
            return ParseList(result.StandardOutput);
        }
        // Before git 2.36, which added -z: the same records, a line each.
        result = await git.RunAsync(folder, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } ? ParseList(result.StandardOutput) : null;
    }

    /// <summary>
    /// The branches named like the ones Claude Code makes for worktrees (<c>worktree-&lt;name&gt;</c>), so a new
    /// worktree's name isn't one a branch left behind already has. Empty when git can't tell.
    /// </summary>
    public async Task<IReadOnlySet<string>> BranchesAsync(string folder, CancellationToken cancellationToken = default)
    {
        var result = await git.RunAsync(folder, ["for-each-ref", "--format=%(refname:short)", $"refs/heads/{BranchPrefix}*"], cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 }
            ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();
    }

    /// <summary>
    /// The worktree at <paramref name="path"/>, among those of the repository <paramref name="folder"/> is in; null when
    /// it isn't one, or git can't tell. The main checkout is never returned.
    /// </summary>
    public async Task<GitWorktree?> FindAsync(string folder, string path, CancellationToken cancellationToken = default)
    {
        if (await ListAsync(folder, cancellationToken).ConfigureAwait(false) is not { } worktrees)
        {
            return null;
        }
        var target = Normalize(path);
        if (worktrees.FirstOrDefault(w => !w.IsMain && Diffs.ChangedFiles.PlatformPathComparer.Equals(w.Path, target)) is { } found)
        {
            return found;
        }
        // Git lists worktrees by their real paths, so one reached through a symbolic link (macOS's /var is
        // /private/var) only matches as git names it. Returned as the caller spells it, to compare with tabs' folders.
        if (await git.GetRepositoryRootAsync(path, cancellationToken).ConfigureAwait(false) is { } real
            && worktrees.FirstOrDefault(w => !w.IsMain && Diffs.ChangedFiles.PlatformPathComparer.Equals(w.Path, Normalize(real))) is { } linked)
        {
            return linked with { Path = target };
        }
        return null;
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
        if (result is not { ExitCode: 0 } || !int.TryParse(result.StandardOutput.Trim(), out var own))
        {
            return null;
        }
        // An ignored folder is one entry: node_modules isn't walked.
        var ignored = await git.RunAsync(worktree.Path, ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--ignored", "--untracked-files=normal"], cancellationToken)
            .ConfigureAwait(false);
        var hasIgnored = ignored is not { ExitCode: 0 } || ignored.StandardOutput.Split('\0').Any(e => e.StartsWith("!! ", StringComparison.Ordinal));
        return new GitWorktreeWork(changes, own, hasIgnored);
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
        var removed = await git.RunAsync(mainFolder, discard ? ["worktree", "remove", "--force", worktree.Path] : ["worktree", "remove", worktree.Path], RemoveTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (removed is not { ExitCode: 0 })
        {
            if (worktree.IsLocked && Directory.Exists(worktree.Path))
            {
                // Still there: locked again, so git worktree prune doesn't forget it.
                await git.RunAsync(mainFolder, ["worktree", "lock", "--reason", "Kept by Claudette: removing it didn't finish", worktree.Path], cancellationToken)
                    .ConfigureAwait(false);
            }
            return removed is null ? "Git couldn't finish removing it." : FirstLine(removed.StandardError) ?? $"git worktree remove failed ({removed.ExitCode}).";
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
