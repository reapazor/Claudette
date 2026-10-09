using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Files;
using Claudette.Core.Json;
using Claudette.Core.Protocol;
using Claudette.Core.Transcripts;

namespace Claudette.Core.History;

/// <summary>
/// Lists the sessions in Claude Code's own session storage on this machine for History (DESIGN.md §9, "History"),
/// including sessions started in the terminal. Transcripts live at
/// <c>&lt;projectsDirectory&gt;/&lt;project-folder&gt;/&lt;session-id&gt;.jsonl</c>; subagent transcripts in
/// <c>&lt;session-id&gt;/subagents/</c> are ignored.
/// <list type="bullet">
/// <item>Summaries are cached by path, length and last-write time, so a rescan only reads files that changed. A file
/// that only grew, as a session's transcript does each turn, is read on from where the last read stopped.</item>
/// <item>Transcripts can be tens of MB, so they're read line by line, and a line is only parsed as JSON when a cheap
/// substring check says it might hold a prompt, an assistant text or a title. Several files are read at once, the most
/// recently written first.</item>
/// <item>The format is internal to Claude Code (DESIGN.md §13, "Transcripts"): unknown entries are skipped, and a file
/// that can't be read is left out of this scan and tried again on the next.</item>
/// </list>
/// </summary>
public sealed class HistoryIndex(string projectsDirectory)
{
    /// <summary>About how many characters of the first prompt a summary keeps.</summary>
    public const int FirstPromptLength = 200;

    /// <summary>How much of each prompt, and of all of a session's prompts together, search looks through.</summary>
    public const int SearchPromptLength = 1_000;

    public const int SearchTextLength = 16_000;

    /// <summary>How many of the most recently written files keep what's needed to read on from where they stopped.</summary>
    internal const int ResumableFiles = 32;

    /// <summary>How many bytes before a read's end are kept, to check the file still has them before reading on.</summary>
    private const int TailLength = 64;

    /// <summary>How many files are read at once, leaving a core for the UI.</summary>
    internal static readonly int Readers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>Transcripts outside Claude Code's storage (the session library's), for <see cref="SummarizeAsync"/>.</summary>
    private readonly Dictionary<string, CacheEntry> _otherCache = new(StringComparer.Ordinal);

    public string ProjectsDirectory { get; } = projectsDirectory;

    /// <summary>Every non-empty session, newest first. Only one scan runs at a time; later callers wait for it.</summary>
    public Task<IReadOnlyList<SessionSummary>> ScanAsync(CancellationToken cancellationToken = default) => ScanAsync(found: null, cancellationToken);

    /// <summary>
    /// Every non-empty session, newest first, telling <paramref name="found"/> about each one as it's read, on the scan's
    /// thread. The most recently written files are read first, so History can show the top of its list before the rest
    /// is read. Only one scan runs at a time; later callers wait for it.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ScanAsync(Action<SessionSummary>? found, CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(found, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>
    /// Summaries of transcripts elsewhere, such as the session library's copies, cached as Claude Code's own are: History
    /// opening again doesn't read them again. Null for one with neither a prompt nor a title, or that can't be read.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SessionSummary?>> SummarizeAsync(IEnumerable<string> transcriptPaths, CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var files = transcriptPaths.Select(path => (Path: path, File: new FileInfo(path))).ToArray();
                // Each file once, however many paths name it: two readers mustn't carry on the same cached read.
                var read = ReadAll(_otherCache, files.Select(f => f.File).DistinctBy(f => f.FullName, StringComparer.Ordinal).ToArray(), cancellationToken)
                    .ToDictionary(r => r.File.FullName, r => r.Summary, StringComparer.Ordinal);
                foreach (var gone in _otherCache.Keys.Where(k => !read.ContainsKey(k)).ToArray())
                {
                    _otherCache.Remove(gone);
                }
                Forget(_otherCache);
                var summaries = new Dictionary<string, SessionSummary?>(StringComparer.Ordinal);
                foreach (var (path, file) in files)
                {
                    summaries[path] = read[file.FullName];
                }
                return (IReadOnlyDictionary<string, SessionSummary?>)summaries;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Sessions whose title or prompts contain every word of <paramref name="query"/>, ignoring case.</summary>
    public static IReadOnlyList<SessionSummary> Filter(IEnumerable<SessionSummary> sessions, string query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? sessions.ToArray()
            : sessions.Where(s => words.All(w => Contains(s.Title, w) || Contains(s.FirstPrompt, w) || Contains(s.Prompts, w))).ToArray();

        static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Reads one transcript. Null when it has neither a prompt nor a title.</summary>
    public static SessionSummary? ReadSummary(string transcriptPath)
    {
        var file = new FileInfo(transcriptPath);
        return Read(file, file.Length, file.LastWriteTimeUtc, resume: null, CancellationToken.None).Summary;
    }

    private IReadOnlyList<SessionSummary> Scan(Action<SessionSummary>? found, CancellationToken cancellationToken)
    {
        // Newest written first, the top of History. The times come with the enumeration, so sorting reads nothing.
        var files = EnumerateTranscripts().OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        var summaries = new List<SessionSummary>();
        foreach (var (_, summary) in ReadAll(_cache, files, cancellationToken))
        {
            if (summary is not null)
            {
                summaries.Add(summary);
                found?.Invoke(summary);
            }
        }

        var seen = files.Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _cache.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _cache.Remove(gone);
        }
        Forget(_cache);

        return summaries
            .OrderByDescending(s => s.LastActivity)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private IEnumerable<FileInfo> EnumerateTranscripts()
    {
        var root = new DirectoryInfo(ProjectsDirectory);
        if (!root.Exists)
        {
            yield break;
        }
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        DirectoryInfo[] projects;
        try
        {
            projects = root.GetDirectories("*", options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var project in projects)
        {
            FileInfo[] files;
            try
            {
                files = project.GetFiles("*.jsonl", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                // "*.jsonl" can also match longer extensions on Windows.
                if (string.Equals(file.Extension, ".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Each file's summary, from <paramref name="cache"/> or read again, in <paramref name="files"/>' order as each is
    /// ready. <see cref="Readers"/> files are read at once; only the calling thread touches the cache.
    /// </summary>
    private static IEnumerable<(FileInfo File, SessionSummary? Summary)> ReadAll(Dictionary<string, CacheEntry> cache, IReadOnlyList<FileInfo> files, CancellationToken cancellationToken)
    {
        var work = files.Select(f => (File: f, Cached: cache.GetValueOrDefault(f.FullName))).ToArray();
        // One file at a time to each reader, since they differ so much in size.
        var results = Partitioner.Create(work, EnumerablePartitionerOptions.NoBuffering)
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Readers)
            .WithMergeOptions(ParallelMergeOptions.NotBuffered)
            .WithCancellation(cancellationToken)
            .Select(w => (w.File, Entry: Refresh(w.File, w.Cached, cancellationToken)));
        foreach (var (file, entry) in results)
        {
            if (entry is null)
            {
                // Not cached, so the next scan tries again. The read may have carried the cached builder on, so the
                // cached entry is no longer right either.
                cache.Remove(file.FullName);
            }
            else
            {
                cache[file.FullName] = entry;
            }
            yield return (file, entry?.Summary);
        }
    }

    /// <summary>
    /// The file's entry: <paramref name="cached"/> if the file hasn't changed, otherwise read, from where the last read
    /// stopped when it only grew. Null when it can't be read. If the file changes while it's read, the next scan sees a
    /// different length or time and reads again.
    /// </summary>
    private static CacheEntry? Refresh(FileInfo file, CacheEntry? cached, CancellationToken cancellationToken)
    {
        try
        {
            // From the enumeration in a scan, so no extra call per file.
            var length = file.Length;
            var lastWrite = file.LastWriteTimeUtc;
            if (cached is not null && cached.Length == length && cached.LastWriteUtc == lastWrite)
            {
                return cached;
            }
            var resume = cached is { Builder: not null } && length > cached.Length && lastWrite >= cached.LastWriteUtc ? cached : null;
            return Read(file, length, lastWrite, resume, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps what's needed to read on only for the most recently written files: older ones rarely grow.</summary>
    private static void Forget(Dictionary<string, CacheEntry> cache)
    {
        foreach (var (path, entry) in cache.Where(e => e.Value.Builder is not null).OrderByDescending(e => e.Value.LastWriteUtc).Skip(ResumableFiles).ToArray())
        {
            cache[path] = entry with { Builder = null };
        }
    }

    private static CacheEntry Read(FileInfo file, long length, DateTime lastWriteUtc, CacheEntry? resume, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var builder = resume is { Builder: { } carried } && TailMatches(stream, resume) ? carried : new SummaryBuilder();
        var start = ReferenceEquals(builder, resume?.Builder) ? resume!.Offset : 0;
        var complete = start;
        var partial = false;
        foreach (var line in Utf8Lines.Read(stream, start, cancellationToken))
        {
            builder.Add(line.Text);
            if (line.End >= 0)
            {
                complete = line.End;
            }
            else
            {
                // Still being written: the builder has it now, so it can't read on from before it later.
                partial = true;
            }
        }
        var summary = builder.Build(Path.GetFileNameWithoutExtension(file.Name), file.FullName, new DateTimeOffset(lastWriteUtc, TimeSpan.Zero));
        return new CacheEntry(length, lastWriteUtc, summary, partial ? null : builder, complete, partial ? [] : ReadTail(stream, complete));
    }

    private static byte[] ReadTail(Stream stream, long end)
    {
        var tail = new byte[(int)Math.Min(TailLength, end)];
        stream.Position = end - tail.Length;
        stream.ReadExactly(tail);
        return tail;
    }

    /// <summary>The file still has what the last read ended with: it was only appended to since, not rewritten.</summary>
    private static bool TailMatches(Stream stream, CacheEntry cached)
    {
        if (stream.Length < cached.Offset)
        {
            return false;
        }
        var tail = new byte[cached.Tail.Length];
        stream.Position = cached.Offset - tail.Length;
        stream.ReadExactly(tail);
        return tail.AsSpan().SequenceEqual(cached.Tail);
    }

    /// <summary>One line, compacted to a single line of about <paramref name="length"/> characters.</summary>
    internal static string Preview(string text, int length = FirstPromptLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, length + 1));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(c);
            if (builder.Length > length)
            {
                break;
            }
        }
        if (builder.Length <= length)
        {
            return builder.ToString();
        }
        var cut = char.IsHighSurrogate(builder[length - 1]) ? length - 1 : length;
        return builder.ToString(0, cut).TrimEnd() + "…";
    }

    /// <param name="Builder">What was read, to read on from <paramref name="Offset"/>; null when that can't be done.</param>
    /// <param name="Offset">Where the last complete line read ends.</param>
    /// <param name="Tail">The bytes just before <paramref name="Offset"/>.</param>
    private sealed record CacheEntry(long Length, DateTime LastWriteUtc, SessionSummary? Summary, SummaryBuilder? Builder = null, long Offset = 0, byte[]? Tail = null)
    {
        public byte[] Tail { get; init; } = Tail ?? [];
    }

    private sealed class SummaryBuilder
    {
        // Claude Code writes compact JSON, so these only match structure: inside a JSON string the quotes are escaped.
        private const string UserType = "\"type\":\"user\"";
        private const string AssistantType = "\"type\":\"assistant\"";
        private const string TextBlock = "\"type\":\"text\"";
        private const string ToolResultBlock = "\"type\":\"tool_result\"";
        private const string AiTitleType = "\"type\":\"ai-title\"";
        private const string CustomTitleType = "\"type\":\"custom-title\"";
        private const string MetaFlag = "\"isMeta\":true";
        private const string SidechainFlag = "\"isSidechain\":true";
        private const string TimestampKey = "\"timestamp\":\"";
        private const string CwdKey = "\"cwd\":\"";

        private string? _folder;
        private string? _aiTitle;
        private string? _customTitle;
        private string? _firstPrompt;
        private readonly StringBuilder _prompts = new();
        private int _count;
        private DateTimeOffset? _lastActivity;
        private string? _gitBranch;
        private string? _version;

        public void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            var sidechain = line.Contains(SidechainFlag, StringComparison.Ordinal);
            var worthParsing =
                line.Contains(AiTitleType, StringComparison.Ordinal)
                || line.Contains(CustomTitleType, StringComparison.Ordinal)
                || (!sidechain
                    && line.Contains(UserType, StringComparison.Ordinal)
                    && !line.Contains(ToolResultBlock, StringComparison.Ordinal)
                    && !line.Contains(MetaFlag, StringComparison.Ordinal))
                || (!sidechain
                    && line.Contains(AssistantType, StringComparison.Ordinal)
                    && line.Contains(TextBlock, StringComparison.Ordinal));
            if (worthParsing && Parse(line) is { } entry)
            {
                AddEntry(entry);
                return;
            }

            // Everything else only contributes its timestamp and folder. The top-level timestamp comes before
            // toolUseResult, and cwd is among the last top-level fields, so search from the matching end.
            if (ExtractString(line, TimestampKey, fromEnd: false) is { } timestamp)
            {
                Touch(timestamp);
            }
            if (_folder is null && ExtractString(line, CwdKey, fromEnd: true) is { Length: > 0 } cwd)
            {
                _folder = cwd;
            }
        }

        public SessionSummary? Build(string sessionId, string path, DateTimeOffset lastWrite)
        {
            var title = _customTitle ?? _aiTitle;
            if (_firstPrompt is null && title is null)
            {
                return null;
            }
            return new SessionSummary(sessionId, path, _folder, title, _firstPrompt, _count, _lastActivity ?? lastWrite, _gitBranch, _version,
                _prompts.Length == 0 ? null : _prompts.ToString());
        }

        private void AddEntry(JsonObject entry)
        {
            if (entry.GetString("timestamp") is { } timestamp)
            {
                Touch(timestamp);
            }
            if (_folder is null && entry.GetString("cwd") is { Length: > 0 } cwd)
            {
                _folder = cwd;
            }
            if (entry.GetBool("isSidechain") == true || entry.GetBool("isMeta") == true)
            {
                return;
            }
            _gitBranch = entry.GetString("gitBranch") ?? _gitBranch;
            _version = entry.GetString("version") ?? _version;

            switch (entry.GetString("type"))
            {
                case "ai-title":
                    _aiTitle = entry.GetString("aiTitle") ?? _aiTitle;
                    break;
                case "custom-title":
                    _customTitle = entry.GetString("customTitle") ?? _customTitle;
                    break;
                case "user" when entry.GetObject("message") is { } message:
                    if (TranscriptPrompts.FromMessage(message) is { } prompt)
                    {
                        _count++;
                        _firstPrompt ??= Preview(prompt);
                        if (_prompts.Length < SearchTextLength)
                        {
                            _prompts.Append(Preview(prompt, SearchPromptLength)).Append('\n');
                        }
                    }
                    break;
                case "assistant" when entry.GetObject("message")?.GetArray("content") is { } content:
                    if (content.OfType<JsonObject>().Any(b => b.GetString("type") == "text" && !string.IsNullOrWhiteSpace(b.GetString("text"))))
                    {
                        _count++;
                    }
                    break;
            }
        }

        private void Touch(string timestamp)
        {
            if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                && (_lastActivity is null || time > _lastActivity))
            {
                _lastActivity = time;
            }
        }

        private static JsonObject? Parse(string line)
        {
            try
            {
                return JsonTree.ParseObject(line);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The string value after <paramref name="key"/> (which ends in the opening quote), without a full parse.</summary>
        private static string? ExtractString(string line, string key, bool fromEnd)
        {
            var at = fromEnd ? line.LastIndexOf(key, StringComparison.Ordinal) : line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }
            var start = at + key.Length;
            var escaped = false;
            for (var end = start; end < line.Length; end++)
            {
                var c = line[end];
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    var raw = line.AsSpan(start, end - start);
                    if (!raw.Contains('\\'))
                    {
                        return raw.ToString();
                    }
                    try
                    {
                        return JsonSerializer.Deserialize<string>(line.AsSpan(start - 1, end - start + 2));
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }
            return null;
        }
    }
}
