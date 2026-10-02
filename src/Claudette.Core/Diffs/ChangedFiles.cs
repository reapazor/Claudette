using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Diffs;

public enum ChangedFileStatus
{
    Added,
    Modified,
    Deleted,
    /// <summary>The file on disk matches its "before" content again (ignoring line endings).</summary>
    Unchanged,
}

/// <summary>A file the session changed with Edit, Write, MultiEdit or NotebookEdit (DESIGN.md §8).</summary>
public sealed class ChangedFile
{
    private readonly List<string> _toolUseIds = [];

    internal ChangedFile(string path, string? before, bool isNew, bool beforeKnown, string toolUseId, DateTimeOffset at)
    {
        Path = path;
        Before = before;
        IsNew = isNew;
        BeforeKnown = beforeKnown;
        FirstChanged = at;
        LastChanged = at;
        _toolUseIds.Add(toolUseId);
    }

    /// <summary>The path as Claude Code reported it on the first change.</summary>
    public string Path { get; }

    /// <summary>
    /// The content before Claude's first change in the session (its <c>originalFile</c>). Null when the file was new,
    /// or when <see cref="BeforeKnown"/> is false.
    /// </summary>
    public string? Before { get; }

    /// <summary>The file didn't exist before Claude's first change.</summary>
    public bool IsNew { get; }

    /// <summary>
    /// False when the first change's result didn't say what the file held before and nothing was saved for it: a
    /// transcript's result for a file over 10,000 characters, a Write over a file too large for Claude Code to diff, or an
    /// unknown result shape.
    /// </summary>
    public bool BeforeKnown { get; }

    public DateTimeOffset FirstChanged { get; }

    public DateTimeOffset LastChanged { get; private set; }

    public int ChangeCount => _toolUseIds.Count;

    /// <summary>The tool calls that changed the file, in order.</summary>
    public IReadOnlyList<string> ToolUseIds => _toolUseIds;

    internal void AddChange(string toolUseId, DateTimeOffset at)
    {
        _toolUseIds.Add(toolUseId);
        if (at > LastChanged)
        {
            LastChanged = at;
        }
    }
}

/// <summary>A file one turn changed (DESIGN.md §8, "Changes per turn"), with what it held before the turn's first change to it.</summary>
/// <param name="Path">The path as Claude Code reported it.</param>
/// <param name="Before">The file before this turn changed it; null when it was new, or when <paramref name="BeforeKnown"/> is false.</param>
/// <param name="IsNew">The turn created it.</param>
public sealed record TurnFileChange(string Path, string? Before, bool IsNew, bool BeforeKnown);

/// <summary>
/// A changed file compared with the disk now. <paramref name="Added"/> and <paramref name="Removed"/> are null when
/// the lines can't be counted: a binary or very large file, or an unknown "before".
/// </summary>
public sealed record ChangedFileState(ChangedFileStatus Status, int? Added, int? Removed, string? Current)
{
    public bool IsBinary { get; init; }

    public bool IsTooLarge { get; init; }
}

/// <summary>
/// The files a tab's session changed, built from its Edit/Write tool calls (DESIGN.md §8). A file's "before" side is
/// the <c>originalFile</c> from Claude's first change to it, so no snapshots are needed. Transcripts leave out a large
/// one, so replays find it in <see cref="Befores"/>.
/// </summary>
/// <remarks>
/// Used from the UI thread only. Tool use ids are remembered, so replaying a transcript that's already been seen
/// doesn't count changes twice.
/// </remarks>
public sealed class ChangedFiles
{
    private readonly TimeProvider _timeProvider;
    private readonly StringComparer _pathComparer;
    private readonly Dictionary<string, (string ToolName, string? Path)> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChangedFile> _byPath;
    private readonly List<ChangedFile> _files = [];
    /// <summary>The files changed since the last <see cref="TakeTurn"/>, by normalized path, in order of first change.</summary>
    private readonly Dictionary<string, TurnFileChange> _turnByPath;
    private readonly List<TurnFileChange> _turn = [];

    /// <param name="timeProvider">Stamps each change.</param>
    /// <param name="pathComparer">
    /// How paths are matched. Defaults to <see cref="PlatformPathComparer"/>; tests pass one to check another OS's rule.
    /// </param>
    public ChangedFiles(TimeProvider timeProvider, StringComparer? pathComparer = null)
    {
        _timeProvider = timeProvider;
        _pathComparer = pathComparer ?? PlatformPathComparer;
        _byPath = new Dictionary<string, ChangedFile>(_pathComparer);
        _turnByPath = new Dictionary<string, TurnFileChange>(_pathComparer);
    }

    /// <summary>Case-insensitive on Windows and macOS, case-sensitive on Linux.</summary>
    public static StringComparer PlatformPathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Files larger than this are listed without line counts or content.</summary>
    public long MaxInspectBytes { get; init; } = TextFiles.DefaultMaxBytes;

    /// <summary>
    /// Where a large file's <c>originalFile</c> is kept for replays, since transcripts leave it out. Null: a replayed
    /// large file's "before" is unknown.
    /// </summary>
    public BeforeContentStore? Befores { get; init; }

    /// <summary>The changed files, in order of first change.</summary>
    public IReadOnlyList<ChangedFile> Files => _files;

    /// <summary>Raised after a change is recorded.</summary>
    public event Action? Changed;

    public static bool IsFileTool(string toolName) => toolName is "Edit" or "Write" or "MultiEdit" or "NotebookEdit";

    /// <summary>Notes a tool call. Only file tools are kept; the change counts once its result succeeds.</summary>
    public void RecordToolUse(string toolUseId, string toolName, JsonObject input)
    {
        if (!IsFileTool(toolName) || _applied.Contains(toolUseId))
        {
            return;
        }
        var path = toolName == "NotebookEdit" ? input.GetString("notebook_path") : input.GetString("file_path");
        _pending[toolUseId] = (toolName, string.IsNullOrEmpty(path) ? null : path);
    }

    /// <summary>
    /// Applies a tool call's result. Errors, and results for tool calls that weren't recorded, are ignored.
    /// </summary>
    /// <param name="toolUseResult">The structured <c>tool_use_result</c> (<c>toolUseResult</c> in transcripts).</param>
    /// <param name="at">When the change happened, for transcript replay; defaults to now.</param>
    /// <param name="inTurn">
    /// The change is part of the turn under way, for <see cref="TakeTurn"/>. A transcript read back has no turns to
    /// show, so its changes aren't.
    /// </param>
    public void RecordToolResult(string toolUseId, bool isError, JsonNode? toolUseResult, DateTimeOffset? at = null, bool inTurn = true)
    {
        if (!_pending.Remove(toolUseId, out var pending) || isError)
        {
            return;
        }
        var result = toolUseResult as JsonObject;
        if (result?.GetString("error") is { Length: > 0 })
        {
            return; // NotebookEdit reports some failures here.
        }
        var path = result?.GetString("filePath") ?? result?.GetString("notebook_path") ?? pending.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _applied.Add(toolUseId);
        var when = at ?? _timeProvider.GetUtcNow();
        var key = Normalize(path);
        if (inTurn && !_turnByPath.ContainsKey(key))
        {
            // What the file held before this turn's first change to it: this change's own "before".
            var (turnBefore, turnNew, turnKnown) = ReadBefore(pending.ToolName, result);
            var change = new TurnFileChange(path, turnBefore, turnNew, turnKnown);
            _turnByPath.Add(key, change);
            _turn.Add(change);
        }
        if (_byPath.TryGetValue(key, out var file))
        {
            file.AddChange(toolUseId, when);
        }
        else
        {
            var (before, isNew, beforeKnown) = ReadBefore(pending.ToolName, result);
            if (!beforeKnown && Befores?.Load(toolUseId) is { } saved)
            {
                (before, beforeKnown) = (saved, true);
            }
            else if (before is { Length: > BeforeContentStore.TranscriptLimit })
            {
                Befores?.Save(toolUseId, before);
            }
            file = new ChangedFile(path, before, isNew, beforeKnown, toolUseId, when);
            _byPath.Add(key, file);
            _files.Add(file);
        }
        Changed?.Invoke();
    }

    /// <summary>The changed file at <paramref name="path"/>, or null when Claude hasn't changed it in this session.</summary>
    public ChangedFile? Find(string path) => _byPath.GetValueOrDefault(Normalize(path));

    /// <summary>
    /// The files changed since the last call, in order of their first change, each with what it held before that change
    /// (DESIGN.md §8, "Changes per turn"). Called as each turn ends, which starts the next.
    /// </summary>
    public IReadOnlyList<TurnFileChange> TakeTurn()
    {
        var turn = _turn.ToArray();
        _turn.Clear();
        _turnByPath.Clear();
        return turn;
    }

    /// <summary>
    /// A turn's change to a file, counted against the file on disk: how the turn left it, when it has just ended. Null
    /// when the lines can't be counted (binary, too large, or the "before" isn't known).
    /// </summary>
    public (int Added, int Removed)? CountAgainstDisk(TurnFileChange change)
    {
        if (!change.BeforeKnown || change.Before is { } before && (LineDiff.LooksBinary(before) || before.Length > MaxInspectBytes))
        {
            return null;
        }
        var read = TextFiles.Read(change.Path, MaxInspectBytes);
        return read.Kind switch
        {
            TextFileKind.Text => LineDiff.Count(change.Before, read.Text),
            TextFileKind.Missing => LineDiff.Count(change.Before, null),
            _ => null,
        };
    }

    /// <summary>Whether two paths name the same file, by this list's rule.</summary>
    public bool SamePath(string a, string b) => _pathComparer.Equals(Normalize(a), Normalize(b));

    /// <summary>Compares a changed file with what's on disk now.</summary>
    public ChangedFileState Inspect(ChangedFile file)
    {
        var read = TextFiles.Read(file.Path, MaxInspectBytes);
        var before = file.Before;
        var beforeBinary = before is not null && LineDiff.LooksBinary(before);
        var beforeTooLarge = before is not null && before.Length > MaxInspectBytes;
        var beforeCountable = file.BeforeKnown && !beforeBinary && !beforeTooLarge;

        switch (read.Kind)
        {
            case TextFileKind.Missing:
                if (file.IsNew)
                {
                    // Created and then removed again: nothing changed overall.
                    return new ChangedFileState(ChangedFileStatus.Unchanged, 0, 0, null);
                }
                return beforeCountable
                    ? new ChangedFileState(ChangedFileStatus.Deleted, 0, LineDiff.CountLines(before), null)
                    : new ChangedFileState(ChangedFileStatus.Deleted, null, null, null) { IsBinary = beforeBinary, IsTooLarge = beforeTooLarge };
            case TextFileKind.TooLarge:
                return new ChangedFileState(file.IsNew ? ChangedFileStatus.Added : ChangedFileStatus.Modified, null, null, null) { IsTooLarge = true };
            case TextFileKind.Binary:
                return new ChangedFileState(file.IsNew ? ChangedFileStatus.Added : ChangedFileStatus.Modified, null, null, null) { IsBinary = true };
            case TextFileKind.Unreadable:
                return new ChangedFileState(file.IsNew ? ChangedFileStatus.Added : ChangedFileStatus.Modified, null, null, null);
        }

        var current = read.Text!;
        if (file.IsNew)
        {
            return new ChangedFileState(ChangedFileStatus.Added, LineDiff.CountLines(current), 0, current);
        }
        if (!beforeCountable)
        {
            return new ChangedFileState(ChangedFileStatus.Modified, null, null, current) { IsBinary = beforeBinary, IsTooLarge = beforeTooLarge };
        }
        if (SameIgnoringLineEndings(before!, current))
        {
            return new ChangedFileState(ChangedFileStatus.Unchanged, 0, 0, current);
        }
        var (added, removed) = LineDiff.Count(before, current);
        return new ChangedFileState(ChangedFileStatus.Modified, added, removed, current);
    }

    /// <summary>
    /// The path to show: relative to <paramref name="workingFolder"/> when the file is inside it, otherwise the full
    /// path. Either way with forward slashes.
    /// </summary>
    public string DisplayPath(ChangedFile file, string workingFolder)
    {
        var path = Normalize(file.Path);
        var folder = Normalize(workingFolder);
        if (folder.Length > 0)
        {
            var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
            if (path.Length > prefix.Length && _pathComparer.Equals(path[..prefix.Length], prefix))
            {
                return path[prefix.Length..].Replace('\\', '/');
            }
        }
        return path.Replace('\\', '/');
    }

    /// <summary>What the file held before the first change, from the tool's structured result.</summary>
    private static (string? Before, bool IsNew, bool BeforeKnown) ReadBefore(string toolName, JsonObject? result)
    {
        if (result is null)
        {
            return (null, false, false);
        }
        if (toolName == "NotebookEdit")
        {
            return result.GetString("original_file") is { } notebook ? (notebook, false, true) : (null, false, false);
        }

        var type = result.GetString("type");
        if (type == "create")
        {
            return (null, true, true);
        }
        if (!result.TryGetPropertyValue("originalFile", out var original))
        {
            return (null, false, false);
        }
        return original switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => (value.GetValue<string>(), false, true),
            // An Edit with nothing to replace creates the file.
            null when result.GetString("oldString") == "" => (null, true, true),
            // Otherwise it was left out: transcripts leave out one over 10,000 characters, and a Write over a file too
            // large to diff reports null.
            _ => (null, false, false),
        };
    }

    internal static string Normalize(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SameIgnoringLineEndings(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }
        return string.Equals(ToLf(a), ToLf(b), StringComparison.Ordinal);

        static string ToLf(string text) => text.Contains('\r') ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
    }
}
