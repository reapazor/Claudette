using Claudette.Core.Git;

namespace Claudette.Core.Composer;

/// <summary>
/// A file or folder in a tab's working folder, relative to it with forward slashes. Folders end in <c>/</c>. Its parts
/// are worked out once, since matching reads them for every path on every keystroke.
/// </summary>
public sealed record IndexedPath
{
    public IndexedPath(string path)
    {
        Path = path;
        IsFolder = path.EndsWith('/');
        var trimmed = IsFolder ? path[..^1] : path;
        var slash = trimmed.LastIndexOf('/');
        Name = trimmed[(slash + 1)..];
        Parent = slash < 0 ? "" : trimmed[..(slash + 1)];
        Depth = trimmed.AsSpan().Count('/');
    }

    public string Path { get; }

    public bool IsFolder { get; }

    /// <summary>The last segment, without a folder's trailing slash.</summary>
    public string Name { get; }

    /// <summary>The folder it's in, with a trailing slash, or empty at the top.</summary>
    public string Parent { get; }

    public int Depth { get; }
}

/// <summary>
/// The files and folders of a tab's working folder, for the composer's <c>@</c> autocomplete (DESIGN.md §5). In a git
/// repository it's what <c>git ls-files</c> lists, so <c>.gitignore</c> is respected; elsewhere, a bounded walk that
/// skips version control and build output folders. Cached, and refreshed when it's older than <see cref="MaxAge"/>.
/// </summary>
public sealed class ProjectFileIndex(string folder, GitWorkingTree git, TimeProvider timeProvider)
{
    /// <summary>Folders the walk never enters: version control, dependencies and build output.</summary>
    public static IReadOnlySet<string> SkippedFolders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".venv",
    };

    private readonly Lock _lock = new();
    private IReadOnlyList<IndexedPath> _paths = [];
    private DateTimeOffset? _builtAt;
    private Task<IReadOnlyList<IndexedPath>>? _building;

    public string Folder { get; } = folder;

    /// <summary>How long a listing is used before it's refreshed.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>At most this many files are kept, with the folders they're in, so a huge folder can't use unbounded memory.</summary>
    public int MaxFiles { get; init; } = 100_000;

    /// <summary>The walk (outside git) stops after this many entries.</summary>
    public int MaxWalkEntries { get; init; } = 20_000;

    /// <summary>The walk doesn't go deeper than this.</summary>
    public int MaxWalkDepth { get; init; } = 16;

    /// <summary>The latest listing, possibly stale; empty until the first one finishes.</summary>
    public IReadOnlyList<IndexedPath> Current
    {
        get
        {
            lock (_lock)
            {
                return _paths;
            }
        }
    }

    /// <summary>Whether the listing came from git (so it respects <c>.gitignore</c>).</summary>
    public bool FromGit { get; private set; }

    public bool IsFresh
    {
        get
        {
            lock (_lock)
            {
                return _builtAt is { } built && timeProvider.GetUtcNow() - built < MaxAge;
            }
        }
    }

    /// <summary>Marks the listing stale, for example after a turn that may have created files.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _builtAt = null;
        }
    }

    /// <summary>The listing, refreshed first when it's stale. Callers arriving during a refresh share it.</summary>
    public Task<IReadOnlyList<IndexedPath>> GetAsync()
    {
        lock (_lock)
        {
            if (_builtAt is { } built && timeProvider.GetUtcNow() - built < MaxAge)
            {
                return Task.FromResult(_paths);
            }
            // Started off the lock's thread, so it can't finish (and clear _building) before it's stored.
            return _building ??= Task.Run(BuildAsync);
        }
    }

    private async Task<IReadOnlyList<IndexedPath>> BuildAsync()
    {
        try
        {
            var files = Directory.Exists(Folder) ? await git.ListFilesAsync(Folder).ConfigureAwait(false) : null;
            var paths = files is not null ? WithFolders(files, MaxFiles) : Walk(Folder, MaxWalkEntries, MaxWalkDepth);
            lock (_lock)
            {
                _paths = paths;
                _builtAt = timeProvider.GetUtcNow();
                FromGit = files is not null;
            }
            return paths;
        }
        finally
        {
            lock (_lock)
            {
                _building = null;
            }
        }
    }

    /// <summary>The files git listed, at most <paramref name="maxFiles"/>, plus the folders they're in, sorted.</summary>
    internal static IReadOnlyList<IndexedPath> WithFolders(IReadOnlyList<string> files, int maxFiles)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var kept = 0;
        foreach (var file in files)
        {
            // Folders don't count: a deep tree of few files would otherwise keep fewer of them.
            if (kept >= maxFiles)
            {
                break;
            }
            var path = file.Replace('\\', '/').TrimStart('/');
            if (path.Length == 0)
            {
                continue;
            }
            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                paths.Add(path[..(slash + 1)]);
            }
            if (paths.Add(path))
            {
                kept++;
            }
        }
        return paths.Order(StringComparer.Ordinal).Select(p => new IndexedPath(p)).ToArray();
    }

    /// <summary>
    /// Lists files and folders breadth first, skipping <see cref="SkippedFolders"/> and links (which could loop), until
    /// <paramref name="maxEntries"/>. Unreadable folders are skipped.
    /// </summary>
    internal static IReadOnlyList<IndexedPath> Walk(string root, int maxEntries, int maxDepth)
    {
        var result = new List<string>();
        var queue = new Queue<(string Absolute, string Relative, int Depth)>();
        queue.Enqueue((root, "", 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
        while (queue.Count > 0 && result.Count < maxEntries)
        {
            var (absolute, relative, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(absolute).EnumerateFileSystemInfos("*", options)
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (result.Count >= maxEntries)
                {
                    break;
                }
                if (entry is DirectoryInfo directory)
                {
                    if (SkippedFolders.Contains(directory.Name))
                    {
                        continue;
                    }
                    var path = relative + directory.Name + "/";
                    result.Add(path);
                    if (depth + 1 < maxDepth)
                    {
                        queue.Enqueue((directory.FullName, path, depth + 1));
                    }
                }
                else
                {
                    result.Add(relative + entry.Name);
                }
            }
        }
        return result.Order(StringComparer.Ordinal).Select(p => new IndexedPath(p)).ToArray();
    }
}
