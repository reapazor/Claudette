namespace Claudette.Core.ProjectTools;

/// <summary>
/// The bounded search every provider uses to find its projects for a tab's folder (DESIGN.md §18, "Project tools"):
/// <list type="bullet">
/// <item>the folder itself;</item>
/// <item>its subfolders, a couple of levels down (for layouts like <c>Game/NightOwl/NightOwl.uproject</c>), never
/// going into build output, caches or version control folders;</item>
/// <item>its parent folders up to the repository's root: a folder with <c>.git</c> or a Perforce config file, or at
/// most six levels up (for a tab opened on <c>Source/</c>).</item>
/// </list>
/// </summary>
public static class ProjectSearch
{
    public const int ChildLevels = 2;

    public const int ParentLevels = 6;

    /// <summary>At most this many folders are looked at below the tab's folder, however wide it is.</summary>
    public const int MaxFolders = 2000;

    /// <summary>
    /// Folders never looked into: engines' and IDEs' output and caches, and the engine itself (a project is never inside
    /// <c>Engine/</c>, and <c>Templates/</c> holds template projects, not the user's).
    /// </summary>
    public static readonly IReadOnlySet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Intermediate", "Saved", "DerivedDataCache", "Binaries", "Content", "Plugins", "Source", "Config",
        "Engine", "Templates", "FeaturePacks",
        "Library", "Temp", "Logs", "obj", "bin", "Packages",
        "node_modules", ".git", ".hg", ".svn", ".vs", ".vscode", ".idea", ".godot", ".import",
    };

    /// <summary>
    /// Runs <paramref name="matchesIn"/> on each folder of the search, in order: the folder, then its subfolders
    /// (shallowest first, by name), then its parents (nearest first). Duplicates are dropped.
    /// </summary>
    /// <param name="matchesIn">The projects in one folder, such as its <c>*.uproject</c> files. It must not throw.</param>
    public static IReadOnlyList<string> Find(string folder, Func<string, IEnumerable<string>> matchesIn, ProjectToolContext context)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(context.OS.PathComparer());
        void Add(IEnumerable<string> matches)
        {
            foreach (var match in matches)
            {
                if (seen.Add(match))
                {
                    found.Add(match);
                }
            }
        }

        if (!Directory.Exists(folder))
        {
            return found;
        }
        Add(Safe(matchesIn, folder));
        foreach (var child in Children(folder))
        {
            Add(Safe(matchesIn, child));
        }
        foreach (var parent in Parents(folder, context))
        {
            Add(Safe(matchesIn, parent));
        }
        return found;
    }

    /// <summary>The subfolders of <paramref name="folder"/>, level by level, skipping hidden and skipped ones.</summary>
    public static IEnumerable<string> Children(string folder, int levels = ChildLevels)
    {
        var level = new List<string> { folder };
        var visited = 0;
        for (var depth = 0; depth < levels && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var parent in level)
            {
                foreach (var child in SubfoldersOf(parent))
                {
                    if (++visited > MaxFolders)
                    {
                        yield break;
                    }
                    next.Add(child);
                    yield return child;
                }
            }
            level = next;
        }
    }

    /// <summary>
    /// The parents of <paramref name="folder"/>, nearest first, up to the repository's root. None when the folder is
    /// itself a repository's root.
    /// </summary>
    public static IEnumerable<string> Parents(string folder, ProjectToolContext context)
    {
        var current = folder;
        for (var level = 0; level < ParentLevels; level++)
        {
            if (IsRepositoryRoot(current, context))
            {
                yield break;
            }
            var parent = SafeParent(current);
            if (parent is null)
            {
                yield break;
            }
            yield return parent;
            current = parent;
        }
    }

    /// <summary>A folder with <c>.git</c> (a folder, or a file in a worktree), <c>.p4config</c> or <c>P4CONFIG</c>'s file.</summary>
    public static bool IsRepositoryRoot(string folder, ProjectToolContext context)
    {
        try
        {
            var git = Path.Combine(folder, ".git");
            return Directory.Exists(git) || File.Exists(git)
                || File.Exists(Path.Combine(folder, ".p4config"))
                || context.P4ConfigName is { Length: > 0 } name && name.IndexOfAny(['/', '\\']) < 0 && File.Exists(Path.Combine(folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Files in <paramref name="folder"/> matching <paramref name="pattern"/>, sorted by name; none if it can't be read.</summary>
    public static IReadOnlyList<string> FilesIn(string folder, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SubfoldersOf(string folder)
    {
        string[] children;
        try
        {
            children = Directory.EnumerateDirectories(folder)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.Length > 0 && name[0] != '.' && !SkippedFolders.Contains(name) && !IsLink(d);
                })
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }
        foreach (var child in children)
        {
            yield return child;
        }
    }

    private static bool IsLink(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? SafeParent(string folder)
    {
        try
        {
            return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Safe(Func<string, IEnumerable<string>> matchesIn, string folder)
    {
        try
        {
            return matchesIn(folder).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
