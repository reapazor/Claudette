using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <item>Summaries are cached by path, length and last-write time, so a rescan only reads files that changed.</item>
/// <item>Transcripts can be tens of MB, so they're read line by line, and a line is only parsed as JSON when a cheap
/// substring check says it might hold a prompt, an assistant text or a title.</item>
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

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public string ProjectsDirectory { get; } = projectsDirectory;

    /// <summary>Every non-empty session, newest first. Only one scan runs at a time; later callers wait for it.</summary>
    public async Task<IReadOnlyList<SessionSummary>> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(cancellationToken), cancellationToken).ConfigureAwait(false);
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
        return Read(file, file.LastWriteTimeUtc, CancellationToken.None);
    }

    private IReadOnlyList<SessionSummary> Scan(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var summaries = new List<SessionSummary>();
        foreach (var file in EnumerateTranscripts())
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(file.FullName);
            // From the enumeration, so no extra call per file. If the file changes while it's read, the next scan
            // sees a different length or time and reads it again.
            var length = file.Length;
            var lastWrite = file.LastWriteTimeUtc;

            if (!_cache.TryGetValue(file.FullName, out var cached) || cached.Length != length || cached.LastWriteUtc != lastWrite)
            {
                try
                {
                    cached = new CacheEntry(length, lastWrite, Read(file, lastWrite, cancellationToken));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not cached, so the next scan tries again.
                    _cache.Remove(file.FullName);
                    continue;
                }
                _cache[file.FullName] = cached;
            }
            if (cached.Summary is { } summary)
            {
                summaries.Add(summary);
            }
        }

        foreach (var gone in _cache.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _cache.Remove(gone);
        }

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

    private static SessionSummary? Read(FileInfo file, DateTime lastWriteUtc, CancellationToken cancellationToken)
    {
        var builder = new SummaryBuilder();
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var count = 0;
            while (reader.ReadLine() is { } line)
            {
                if (++count % 256 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                builder.Add(line);
            }
        }
        return builder.Build(Path.GetFileNameWithoutExtension(file.Name), file.FullName, new DateTimeOffset(lastWriteUtc, TimeSpan.Zero));
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

    private sealed record CacheEntry(long Length, DateTime LastWriteUtc, SessionSummary? Summary);

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
