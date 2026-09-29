using System.Text.Json;
using Claudette.Core.Settings;

namespace Claudette.Core.Library;

/// <summary>One transcript in the library, as History lists it.</summary>
/// <param name="Record">The session's record. A conflict copy shares its folder's record.</param>
/// <param name="TranscriptPath">The transcript in the library.</param>
/// <param name="IsConflictCopy">A copy a sync client made when two machines changed the transcript at once.</param>
/// <param name="ConflictLabel">What tells the copy apart, for example <c>copy 1</c> or <c>DESKTOP-01's conflicted copy 2026-09-28</c>.</param>
public sealed record LibraryEntry(SessionRecord Record, string TranscriptPath, bool IsConflictCopy, string? ConflictLabel);

/// <summary>
/// Claudette's session library (DESIGN.md §9, "Session library (sync across machines)"): a folder the user chooses,
/// possibly one kept up to date by Google Drive, Dropbox or OneDrive. Claudette only reads and writes files; the sync
/// client does the rest. Layout:
/// <code>
/// &lt;library&gt;/sessions/&lt;session-id&gt;/record.json
///                                   &lt;session-id&gt;.jsonl
///                                   subagents/*.jsonl
///                                   lease.json
/// &lt;library&gt;/settings-sync.json
/// </code>
/// Files are written to a temporary name and then renamed, so a sync client never uploads a half-written file.
/// </summary>
public sealed class SessionLibrary(string libraryFolder, TimeProvider time)
{
    public const string SessionsFolderName = "sessions";
    public const string RecordFileName = "record.json";
    public const string SubagentsFolderName = "subagents";
    public const string SettingsSyncFileName = "settings-sync.json";

    private const string TranscriptExtension = ".jsonl";

    private static JsonSerializerOptions Json => JsonFileStore<SessionRecord>.Options;

    public string LibraryFolder { get; } = libraryFolder;

    public string SessionsFolder => Path.Combine(LibraryFolder, SessionsFolderName);

    /// <summary>Where synced settings are kept (DESIGN.md §14, "Settings sync").</summary>
    public string SettingsSyncFile => Path.Combine(LibraryFolder, SettingsSyncFileName);

    public string GetSessionFolder(string sessionId) => Path.Combine(SessionsFolder, CheckId(sessionId));

    /// <summary>
    /// Copies a session into the library after a turn finishes: the transcript, then the subagent transcripts, then
    /// <c>record.json</c> last, so a record in the library means its transcript is there too. Files that haven't
    /// changed (same length and last-write time) aren't copied again. The source files are never modified.
    /// </summary>
    /// <param name="transcriptPath">Claude Code's <c>&lt;session-id&gt;.jsonl</c>.</param>
    /// <param name="subagentsDirectory">Claude Code's <c>&lt;session-id&gt;/subagents</c> folder, if there is one.</param>
    public async Task SaveAsync(SessionRecord record, string transcriptPath, string? subagentsDirectory, CancellationToken cancellationToken = default)
    {
        var folder = GetSessionFolder(record.SessionId);
        // Serialized first: the caller may change the record while the files copy.
        var json = JsonSerializer.Serialize(record, Json);

        await LibraryFiles.CopyIfChangedAsync(transcriptPath, Path.Combine(folder, TranscriptName(record.SessionId)), cancellationToken).ConfigureAwait(false);
        if (subagentsDirectory is not null && Directory.Exists(subagentsDirectory))
        {
            await CopyTranscriptsAsync(subagentsDirectory, Path.Combine(folder, SubagentsFolderName), cancellationToken).ConfigureAwait(false);
        }
        await LibraryFiles.WriteTextAsync(Path.Combine(folder, RecordFileName), json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every session folder with a readable record and transcript, newest first. Conflict copies that a sync client
    /// made of a transcript (<c>abc (1).jsonl</c>, <c>abc (DESKTOP-01's conflicted copy 2026-09-28).jsonl</c>,
    /// <c>abc-DESKTOP-01.jsonl</c>) are listed as entries of their own, right after the original; they're never merged.
    /// Folders that can't be read are skipped.
    /// </summary>
    public IReadOnlyList<LibraryEntry> List()
    {
        var entries = new List<LibraryEntry>();
        foreach (var folder in SessionFolders())
        {
            try
            {
                AddEntries(folder, entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip it; it may be mid-sync.
            }
        }
        return entries
            .OrderByDescending(e => e.Record.LastUsed)
            .ThenBy(e => e.Record.SessionId, StringComparer.Ordinal)
            .ThenBy(e => e.IsConflictCopy)
            .ThenBy(e => e.ConflictLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The library's copy of a session's transcript, or null when there isn't one.</summary>
    public string? GetTranscriptPath(string sessionId)
    {
        if (!IsValidId(sessionId))
        {
            return null;
        }
        var path = Path.Combine(GetSessionFolder(sessionId), TranscriptName(sessionId));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Copies a session's transcript to the local working folder as <c>&lt;session-id&gt;.jsonl</c>, with its subagent
    /// transcripts in <c>&lt;session-id&gt;/subagents/</c> beside it, and returns the local path. The app resumes with
    /// <c>claude --resume &lt;that path&gt;</c>; Claude Code then keeps writing to the local file, never to the library.
    /// </summary>
    /// <exception cref="FileNotFoundException">The library has no transcript for the session.</exception>
    public async Task<string> CopyToLocalAsync(string sessionId, string localSessionsFolder, CancellationToken cancellationToken = default)
    {
        var folder = GetSessionFolder(sessionId);
        var source = GetTranscriptPath(sessionId)
            ?? throw new FileNotFoundException($"The session library has no transcript for session {sessionId}.", Path.Combine(folder, TranscriptName(sessionId)));
        var target = Path.Combine(localSessionsFolder, TranscriptName(sessionId));
        await LibraryFiles.CopyIfChangedAsync(source, target, cancellationToken).ConfigureAwait(false);

        var subagents = Path.Combine(folder, SubagentsFolderName);
        if (Directory.Exists(subagents))
        {
            await CopyTranscriptsAsync(subagents, Path.Combine(localSessionsFolder, sessionId, SubagentsFolderName), cancellationToken).ConfigureAwait(false);
        }
        return target;
    }

    /// <summary>
    /// Copies a transcript Claude Code keeps under another folder's project, with its subagents' transcripts, to the
    /// local working folder, so the session can go on in a different folder with <c>--resume &lt;path&gt;</c>: Claude
    /// Code finds sessions by folder, so <c>--resume &lt;id&gt;</c> wouldn't (DESIGN.md §9, "Missing folder"). Returns
    /// the working copy; a transcript that already is one is left where it is.
    /// </summary>
    public static async Task<string> CopyToWorkingFolderAsync(string transcript, string sessionId, string localSessionsFolder, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(localSessionsFolder, TranscriptName(CheckId(sessionId)));
        if (string.Equals(Path.GetFullPath(transcript), Path.GetFullPath(target), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }
        await LibraryFiles.CopyIfChangedAsync(transcript, target, cancellationToken).ConfigureAwait(false);
        var subagents = Path.Combine(Path.GetDirectoryName(transcript)!, sessionId, SubagentsFolderName);
        if (Directory.Exists(subagents))
        {
            await CopyTranscriptsAsync(subagents, Path.Combine(localSessionsFolder, sessionId, SubagentsFolderName), cancellationToken).ConfigureAwait(false);
        }
        return target;
    }

    /// <summary>
    /// Library retention (Settings → Sessions): deletes session folders last used longer ago than
    /// <paramref name="keepFor"/>. Kept regardless: the ids in <paramref name="keep"/>, folders with a live (not stale)
    /// lease, and folders whose record can't be read. A null <paramref name="keepFor"/> keeps everything.
    /// </summary>
    /// <returns>How many sessions were deleted.</returns>
    public int Prune(TimeSpan? keepFor, IReadOnlySet<string> keep)
    {
        if (keepFor is not { } period)
        {
            return 0;
        }
        var now = time.GetUtcNow();
        var cutoff = now - period;
        var deleted = 0;
        foreach (var folder in SessionFolders())
        {
            try
            {
                var id = Path.GetFileName(folder);
                if (keep.Contains(id)
                    || ReadRecord(folder) is not { } record
                    || keep.Contains(record.SessionId)
                    || record.LastUsed >= cutoff
                    || LeaseManager.ReadLease(folder) is { } lease && now - lease.UpdatedAt < LeaseManager.StaleAfter)
                {
                    continue;
                }
                Directory.Delete(folder, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try again next time.
            }
        }
        return deleted;
    }

    /// <summary>
    /// "Move library…" (DESIGN.md §14): copies everything to the new folder, leaving the old one as it is. Files the
    /// new folder already has unchanged aren't copied again.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="to"/> is <paramref name="from"/> or inside it.</exception>
    public static async Task CopyLibraryAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        if (IsSameOrInside(target, source))
        {
            throw new ArgumentException("The new library folder can't be the current one or inside it.", nameof(to));
        }
        Directory.CreateDirectory(target);
        if (!Directory.Exists(source))
        {
            return;
        }
        var files = Directory.GetFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
        foreach (var file in files)
        {
            if (LibraryFiles.IsTemp(file))
            {
                continue;
            }
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            await LibraryFiles.CopyIfChangedAsync(file, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside a folder kept in sync by Google Drive, Dropbox, OneDrive, iCloud Drive
    /// or Box, and which one (DESIGN.md §9, "Privacy").
    /// </summary>
    public static bool IsInCloudSyncFolder(string path, out string? provider) =>
        IsInCloudSyncFolder(path, out provider, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);

    /// <inheritdoc cref="IsInCloudSyncFolder(string, out string?)"/>
    /// <param name="homeFolder">The user's home folder.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable (OneDrive sets <c>OneDrive</c> and friends).</param>
    public static bool IsInCloudSyncFolder(string path, out string? provider, string homeFolder, Func<string, string?> getEnvironmentVariable) =>
        CloudSyncFolders.IsInCloudSyncFolder(path, homeFolder, getEnvironmentVariable, out provider);

    /// <summary>
    /// What tells a sync client's conflict copy apart from the original <c>&lt;id&gt;.jsonl</c>, or null when
    /// <paramref name="fileName"/> isn't a copy of it.
    /// </summary>
    internal static string? ConflictLabel(string sessionId, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length <= sessionId.Length || !stem.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var extra = stem[sessionId.Length..].Trim();
        extra = extra.StartsWith('(') && extra.EndsWith(')')
            ? extra[1..^1].Trim()
            : extra.TrimStart('-', '_', '.', ' ');
        if (extra.Length == 0)
        {
            return null;
        }
        return extra.All(char.IsAsciiDigit) ? $"copy {extra}" : extra;
    }

    private void AddEntries(string folder, List<LibraryEntry> entries)
    {
        var id = Path.GetFileName(folder);
        if (!IsValidId(id) || ReadRecord(folder) is not { } record)
        {
            return;
        }
        var mainName = TranscriptName(id);
        var main = Path.Combine(folder, mainName);
        if (File.Exists(main))
        {
            entries.Add(new LibraryEntry(record, main, IsConflictCopy: false, ConflictLabel: null));
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*" + TranscriptExtension))
        {
            var name = Path.GetFileName(file);
            if (IsTranscript(file) && !name.Equals(mainName, StringComparison.OrdinalIgnoreCase) && ConflictLabel(id, name) is { } label)
            {
                entries.Add(new LibraryEntry(record, file, IsConflictCopy: true, label));
            }
        }
    }

    /// <summary>The folder's <c>record.json</c>; if that can't be read, the newest readable conflict copy of it.</summary>
    private static SessionRecord? ReadRecord(string folder)
    {
        var id = Path.GetFileName(folder);
        var record = TryReadRecord(Path.Combine(folder, RecordFileName));
        if (record is null)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "record*.json"))
            {
                if (!Path.GetFileName(file).Equals(RecordFileName, StringComparison.OrdinalIgnoreCase)
                    && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && TryReadRecord(file) is { } copy
                    && (record is null || copy.LastUsed > record.LastUsed))
                {
                    record = copy;
                }
            }
        }
        if (record is null)
        {
            return null;
        }
        if (string.IsNullOrEmpty(record.SessionId))
        {
            record.SessionId = id;
        }
        record.Tokens ??= new();
        return record;
    }

    private static SessionRecord? TryReadRecord(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private IEnumerable<string> SessionFolders()
    {
        try
        {
            return Directory.Exists(SessionsFolder) ? Directory.GetDirectories(SessionsFolder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task CopyTranscriptsAsync(string fromFolder, string toFolder, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.GetFiles(fromFolder, "*" + TranscriptExtension).Where(IsTranscript))
        {
            await LibraryFiles.CopyIfChangedAsync(file, Path.Combine(toFolder, Path.GetFileName(file)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string TranscriptName(string sessionId) => sessionId + TranscriptExtension;

    private static bool IsTranscript(string path) => path.EndsWith(TranscriptExtension, StringComparison.OrdinalIgnoreCase);

    private static string CheckId(string sessionId) =>
        IsValidId(sessionId) ? sessionId : throw new ArgumentException($"'{sessionId}' isn't a valid session id.", nameof(sessionId));

    /// <summary>Session ids become folder names, so they mustn't be able to point anywhere else.</summary>
    private static bool IsValidId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId)
        && sessionId is not ("." or "..")
        && sessionId.IndexOfAny(['/', '\\', ':']) < 0
        && sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsSameOrInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
            || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
