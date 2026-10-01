using System.Text.Json;
using Claudette.Core.Git;
using Claudette.Core.Library;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Library;

public sealed class SessionLibraryTests : IDisposable
{
    private const string Id = "5d3c1a2b-0000-4000-8000-000000000001";

    private readonly TempFolder _root = new("claudette-library");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionLibrary _library;

    public SessionLibraryTests() => _library = new SessionLibrary(_root.Combine("library"), _time);

    public void Dispose() => _root.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SessionRecord Record(string id = Id, DateTimeOffset? lastUsed = null)
    {
        var record = new SessionRecord
        {
            SessionId = id,
            Name = "Fix login",
            AutoName = "Login redirect fix",
            UserName = "Fix login",
            Model = "opus",
            Effort = "high",
            PermissionMode = "acceptEdits",
            Machine = "DESKTOP-01",
            LastUsed = lastUsed ?? _time.GetUtcNow(),
            Folder = @"D:\Repos\api",
            Project = new ProjectIdentity("git@github.com:owner/api.git", "feature/auth", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", "src"),
            HadUncommittedChanges = true,
            FirstPrompt = "Fix the login redirect",
            ClaudeCodeVersion = "2.1.284",
        };
        record.Tokens.Models["claude-opus-5-5"] = new ModelTokenTotals { Input = 100, Output = 20 };
        record.Tokens.Turns = 1;
        return record;
    }

    /// <summary>Claude Code's files for a session: the transcript and one subagent transcript.</summary>
    private (string Transcript, string Subagents) LocalSession(string id = Id, string content = "{\"type\":\"user\"}\n")
    {
        var transcript = _root.Write($"projects/p/{id}.jsonl", content);
        _root.Write($"projects/p/{id}/subagents/agent-a1.jsonl", "{\"type\":\"assistant\"}\n");
        _root.Write($"projects/p/{id}/subagents/agent-a1.meta.txt", "not a transcript");
        return (transcript, _root.Combine("projects", "p", id, "subagents"));
    }

    private string[] FilesInLibrary() =>
        Directory.GetFiles(_library.LibraryFolder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_library.LibraryFolder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task Save_copies_the_transcript_subagents_and_record_without_leaving_temp_files()
    {
        var (transcript, subagents) = LocalSession();
        var sourceTime = File.GetLastWriteTimeUtc(transcript);

        await _library.SaveAsync(Record(), transcript, subagents, Ct);

        Assert.Equal(
            [$"sessions/{Id}/{Id}.jsonl", $"sessions/{Id}/record.json", $"sessions/{Id}/subagents/agent-a1.jsonl"],
            FilesInLibrary());
        var folder = _library.GetSessionFolder(Id);
        Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(Path.Combine(folder, $"{Id}.jsonl")));
        Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(Path.Combine(folder, $"{Id}.jsonl")));
        // The source is untouched.
        Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(transcript));
        Assert.Equal("{\"type\":\"user\"}\n", File.ReadAllText(transcript));

        var record = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(Path.Combine(folder, "record.json")), JsonFileStore<SessionRecord>.Options)!;
        Assert.Equal("Fix login", record.Name);
        Assert.Equal(120, record.Tokens.Total);
        Assert.Equal("feature/auth", record.Project!.Branch);
        Assert.True(record.HadUncommittedChanges);
        Assert.Equal(_time.GetUtcNow(), record.LastUsed);
        Assert.Contains("\"sessionId\"", File.ReadAllText(Path.Combine(folder, "record.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_skips_a_transcript_that_has_not_changed_and_copies_one_that_has()
    {
        var (transcript, _) = LocalSession();
        await _library.SaveAsync(Record(), transcript, null, Ct);
        var copy = Path.Combine(_library.GetSessionFolder(Id), $"{Id}.jsonl");

        // Same length and last-write time as the source: not copied again.
        var copyTime = File.GetLastWriteTimeUtc(copy);
        File.WriteAllText(copy, "{\"type\":\"xxxx\"}\n");
        File.SetLastWriteTimeUtc(copy, copyTime);
        await _library.SaveAsync(Record(), transcript, null, Ct);
        Assert.Equal("{\"type\":\"xxxx\"}\n", File.ReadAllText(copy));

        File.AppendAllText(transcript, "{\"type\":\"assistant\"}\n");
        await _library.SaveAsync(Record(), transcript, null, Ct);
        Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(copy));
        Assert.DoesNotContain(FilesInLibrary(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_transcript_that_grew_has_only_its_new_lines_added_to_the_library_copy()
    {
        var (transcript, subagents) = LocalSession();
        await _library.SaveAsync(Record(), transcript, subagents, Ct);
        var copy = Path.Combine(_library.GetSessionFolder(Id), $"{Id}.jsonl");
        var agentCopy = Path.Combine(_library.GetSessionFolder(Id), "subagents", "agent-a1.jsonl");
        File.AppendAllText(transcript, "{\"type\":\"assistant\"}\n");
        File.AppendAllText(Path.Combine(subagents, "agent-a1.jsonl"), "{\"type\":\"user\"}\n");

        // Held open as a reader would: a file renamed over this one wouldn't show through it, an appended one does.
        using (var held = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await _library.SaveAsync(Record(), transcript, subagents, Ct);

            Assert.Equal(File.ReadAllText(transcript), new StreamReader(held).ReadToEnd());
        }
        Assert.Equal(File.GetLastWriteTimeUtc(transcript), File.GetLastWriteTimeUtc(copy));
        Assert.Equal(File.ReadAllText(Path.Combine(subagents, "agent-a1.jsonl")), File.ReadAllText(agentCopy));
        Assert.DoesNotContain(FilesInLibrary(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_library_copy_that_is_not_the_start_of_the_transcript_is_written_whole()
    {
        var (transcript, _) = LocalSession();
        await _library.SaveAsync(Record(), transcript, null, Ct);
        var copy = Path.Combine(_library.GetSessionFolder(Id), $"{Id}.jsonl");
        File.WriteAllText(copy, "{\"type\":\"xxxx\"}\n");
        File.AppendAllText(transcript, "{\"type\":\"assistant\"}\n");

        if (OperatingSystem.IsWindows())
        {
            // Windows never renames over a file another handle has open, so there's no reader to keep the old copy.
            await _library.SaveAsync(Record(), transcript, null, Ct);
        }
        else
        {
            // Replaced rather than rewritten in place: a reader that has the old copy open still reads the old copy.
            using var held = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await _library.SaveAsync(Record(), transcript, null, Ct);
            Assert.Equal("{\"type\":\"xxxx\"}\n", new StreamReader(held).ReadToEnd());
        }
        Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(copy));

        // A long one too, where only the end differs.
        var lines = string.Concat(Enumerable.Range(0, 5000).Select(i => $"{{\"line\":{i}}}\n"));
        File.WriteAllText(transcript, lines);
        await _library.SaveAsync(Record(), transcript, null, Ct);
        File.WriteAllText(copy, lines[..^3] + "8}\n");
        File.AppendAllText(transcript, "{\"type\":\"assistant\"}\n");
        await _library.SaveAsync(Record(), transcript, null, Ct);
        Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(copy));
    }

    [Fact]
    public async Task A_forced_save_copies_every_file_again_even_ones_that_look_unchanged()
    {
        var (transcript, subagents) = LocalSession();
        await _library.SaveAsync(Record(), transcript, subagents, Ct);
        var folder = _library.GetSessionFolder(Id);
        var copy = Path.Combine(folder, $"{Id}.jsonl");
        var agentCopy = Path.Combine(folder, "subagents", "agent-a1.jsonl");
        // Changed in the library, but with the same length and last-write time: an ordinary save leaves them.
        ReplaceKeepingTime(copy, "{\"type\":\"xxxx\"}\n");
        ReplaceKeepingTime(agentCopy, "{\"type\":\"xxxxxxxxx\"}\n");
        await _library.SaveAsync(Record(), transcript, subagents, Ct);
        Assert.Equal("{\"type\":\"xxxx\"}\n", File.ReadAllText(copy));

        await _library.SaveAsync(Record(), transcript, subagents, force: true, Ct);

        Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(copy));
        Assert.Equal("{\"type\":\"assistant\"}\n", File.ReadAllText(agentCopy));
        Assert.Equal(File.GetLastWriteTimeUtc(transcript), File.GetLastWriteTimeUtc(copy));
        Assert.DoesNotContain(FilesInLibrary(), f => f.EndsWith(".tmp", StringComparison.Ordinal));

        static void ReplaceKeepingTime(string path, string text)
        {
            var time = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, time);
        }
    }

    [Fact]
    public async Task Save_rejects_an_id_that_is_not_a_folder_name()
    {
        var (transcript, _) = LocalSession();

        await Assert.ThrowsAsync<ArgumentException>(() => _library.SaveAsync(Record("../escape"), transcript, null, Ct));
    }

    [Fact]
    public async Task List_returns_sessions_newest_first_with_their_records()
    {
        var (older, _) = LocalSession("older");
        var (newer, _) = LocalSession("newer");
        await _library.SaveAsync(Record("older", _time.GetUtcNow().AddDays(-2)), older, null, Ct);
        await _library.SaveAsync(Record("newer"), newer, null, Ct);

        var entries = _library.List();

        Assert.Equal(["newer", "older"], entries.Select(e => e.Record.SessionId));
        Assert.All(entries, e => Assert.False(e.IsConflictCopy));
        Assert.Equal(Path.Combine(_library.GetSessionFolder("newer"), "newer.jsonl"), entries[0].TranscriptPath);
        Assert.Equal("Fix login", entries[0].Record.Name);
    }

    [Fact]
    public async Task List_returns_conflict_copies_as_separate_entries()
    {
        var (transcript, _) = LocalSession("abc");
        await _library.SaveAsync(Record("abc"), transcript, null, Ct);
        var folder = _library.GetSessionFolder("abc");
        File.WriteAllText(Path.Combine(folder, "abc (1).jsonl"), "{}");
        File.WriteAllText(Path.Combine(folder, "abc (DESKTOP-01's conflicted copy 2026-09-28).jsonl"), "{}");
        File.WriteAllText(Path.Combine(folder, "abc-DESKTOP-01.jsonl"), "{}");
        File.WriteAllText(Path.Combine(folder, "record (1).json"), "{}");
        File.WriteAllText(Path.Combine(folder, "abc.jsonl.0123.tmp"), "{}");
        File.WriteAllText(Path.Combine(folder, "notes.jsonl"), "{}");

        var entries = _library.List();

        Assert.Equal(4, entries.Count);
        Assert.False(entries[0].IsConflictCopy);
        Assert.Equal(Path.Combine(folder, "abc.jsonl"), entries[0].TranscriptPath);
        Assert.Equal(
            ["copy 1", "DESKTOP-01", "DESKTOP-01's conflicted copy 2026-09-28"],
            entries.Skip(1).Select(e => e.ConflictLabel));
        Assert.All(entries.Skip(1), e =>
        {
            Assert.True(e.IsConflictCopy);
            Assert.Equal("abc", e.Record.SessionId);
        });
    }

    [Fact]
    public async Task List_skips_folders_without_a_readable_record_and_falls_back_to_a_record_copy()
    {
        _root.Write("library/sessions/no-record/no-record.jsonl", "{}");
        _root.Write("library/sessions/bad-record/bad-record.jsonl", "{}");
        _root.Write("library/sessions/bad-record/record.json", "{ not json");
        _root.Write("library/sessions/copied/copied.jsonl", "{}");
        _root.Write("library/sessions/copied/record.json", "{ half written");
        _root.Write("library/sessions/copied/record (1).json", "{\"name\":\"From the copy\",\"lastUsed\":\"2026-09-01T00:00:00Z\"}");
        _root.Write("library/sessions/no-transcript/record.json", "{\"sessionId\":\"no-transcript\"}");
        var (transcript, _) = LocalSession();
        await _library.SaveAsync(Record(), transcript, null, Ct);

        var entries = _library.List();

        Assert.Equal([Id, "copied"], entries.Select(e => e.Record.SessionId));
        Assert.Equal("From the copy", entries[1].Record.Name);
    }

    [Fact]
    public async Task Get_transcript_path()
    {
        var (transcript, _) = LocalSession();
        await _library.SaveAsync(Record(), transcript, null, Ct);

        Assert.Equal(Path.Combine(_library.GetSessionFolder(Id), $"{Id}.jsonl"), _library.GetTranscriptPath(Id));
        Assert.Null(_library.GetTranscriptPath("missing"));
        Assert.Null(_library.GetTranscriptPath("../x"));
    }

    [Fact]
    public async Task Copy_to_local_puts_the_transcript_and_subagents_in_the_working_folder()
    {
        var (transcript, subagents) = LocalSession();
        await _library.SaveAsync(Record(), transcript, subagents, Ct);
        var local = _root.Combine("app-data", "sessions");

        var path = await _library.CopyToLocalAsync(Id, local, Ct);

        Assert.Equal(Path.Combine(local, $"{Id}.jsonl"), path);
        Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(path));
        Assert.True(File.Exists(Path.Combine(local, Id, "subagents", "agent-a1.jsonl")));
        Assert.Empty(Directory.GetFiles(local, "*.tmp", SearchOption.AllDirectories));

        // A newer library copy (say, from another machine) replaces the local one.
        File.AppendAllText(Path.Combine(_library.GetSessionFolder(Id), $"{Id}.jsonl"), "{\"more\":1}\n");
        await _library.CopyToLocalAsync(Id, local, Ct);
        Assert.EndsWith("{\"more\":1}\n", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_to_local_keeps_a_working_copy_with_turns_the_library_missed()
    {
        var (transcript, subagents) = LocalSession();
        await _library.SaveAsync(Record(), transcript, subagents, Ct);
        var local = _root.Combine("app-data", "sessions");
        var path = await _library.CopyToLocalAsync(Id, local, Ct);
        var libraryCopy = Path.Combine(_library.GetSessionFolder(Id), $"{Id}.jsonl");
        var localAgent = Path.Combine(local, Id, "subagents", "agent-a1.jsonl");

        // Turns went on here, and copying them to the library afterwards failed.
        File.AppendAllText(path, "{\"later\":1}\n");
        File.AppendAllText(localAgent, "{\"later\":1}\n");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(libraryCopy).AddMinutes(5));
        await _library.CopyToLocalAsync(Id, local, Ct);
        Assert.EndsWith("{\"later\":1}\n", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.EndsWith("{\"later\":1}\n", File.ReadAllText(localAgent), StringComparison.Ordinal);

        // Another machine carried it on since: the library's is newer, even if it went back to an earlier point.
        File.WriteAllText(libraryCopy, "{\"elsewhere\":1}\n");
        File.SetLastWriteTimeUtc(libraryCopy, File.GetLastWriteTimeUtc(path).AddMinutes(5));
        await _library.CopyToLocalAsync(Id, local, Ct);
        Assert.Equal("{\"elsewhere\":1}\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Copy_to_local_leaves_out_a_line_still_being_added()
    {
        var (transcript, subagents) = LocalSession();
        await _library.SaveAsync(Record(), transcript, subagents, Ct);
        var libraryCopy = Path.Combine(_library.GetSessionFolder(Id), $"{Id}.jsonl");
        var local = _root.Combine("app-data", "sessions");

        // A sync client brought the library's copy over while a line was being appended to it.
        File.AppendAllText(libraryCopy, "{\"type\":\"assis");
        var path = await _library.CopyToLocalAsync(Id, local, Ct);
        Assert.Equal("{\"type\":\"user\"}\n", File.ReadAllText(path));

        // The rest of the line arrives: the next copy has it.
        File.AppendAllText(libraryCopy, "tant\"}\n");
        File.SetLastWriteTimeUtc(libraryCopy, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        await _library.CopyToLocalAsync(Id, local, Ct);
        Assert.Equal("{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Copy_to_local_throws_when_the_library_has_no_transcript()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _library.CopyToLocalAsync("missing", _root.Combine("local"), Ct));
    }

    [Fact]
    public async Task Prune_deletes_old_sessions_except_kept_ones_and_live_or_unreadable_leases()
    {
        var now = _time.GetUtcNow();
        async Task Save(string id, TimeSpan age)
        {
            var (transcript, _) = LocalSession(id);
            await _library.SaveAsync(Record(id, now - age), transcript, null, Ct);
        }
        await Save("recent", TimeSpan.FromDays(1));
        await Save("old", TimeSpan.FromDays(40));
        await Save("old-kept", TimeSpan.FromDays(40));
        await Save("old-leased", TimeSpan.FromDays(40));
        await Save("old-stale-lease", TimeSpan.FromDays(40));
        await Save("old-unreadable-lease", TimeSpan.FromDays(40));
        File.WriteAllText(Path.Combine(_library.GetSessionFolder("old-unreadable-lease"), "lease.json"), "{ half");
        File.WriteAllText(Path.Combine(_library.GetSessionFolder("old-leased"), "lease.json"),
            $"{{\"machine\":\"LAPTOP\",\"updatedAt\":\"{now.AddMinutes(-2):O}\",\"owner\":\"x\"}}");
        File.WriteAllText(Path.Combine(_library.GetSessionFolder("old-stale-lease"), "lease.json"),
            $"{{\"machine\":\"LAPTOP\",\"updatedAt\":\"{now.AddMinutes(-30):O}\",\"owner\":\"x\"}}");
        _root.Write("library/sessions/unreadable/record.json", "nope");

        Assert.Equal(0, _library.Prune(null, new HashSet<string>()));
        var deleted = _library.Prune(TimeSpan.FromDays(30), new HashSet<string> { "old-kept" });

        Assert.Equal(2, deleted);
        Assert.Equal(
            ["old-kept", "old-leased", "old-unreadable-lease", "recent", "unreadable"],
            Directory.GetDirectories(_library.SessionsFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Copy_library_copies_everything_and_keeps_the_old_folder()
    {
        var (transcript, subagents) = LocalSession();
        await _library.SaveAsync(Record(), transcript, subagents, Ct);
        _root.Write("library/settings-sync.json", "{\"version\":1,\"values\":{}}");
        _root.Write("library/sessions/leftover.jsonl.abc.tmp", "partial");
        var target = _root.Combine("new-library");

        await SessionLibrary.CopyLibraryAsync(_library.LibraryFolder, target, Ct);

        var moved = new SessionLibrary(target, _time);
        Assert.Equal(Id, Assert.Single(moved.List()).Record.SessionId);
        Assert.True(File.Exists(Path.Combine(target, "settings-sync.json")));
        Assert.True(File.Exists(Path.Combine(target, "sessions", Id, "subagents", "agent-a1.jsonl")));
        Assert.Empty(Directory.GetFiles(target, "*.tmp", SearchOption.AllDirectories));
        Assert.Single(_library.List());

        // Running it again is harmless.
        await SessionLibrary.CopyLibraryAsync(_library.LibraryFolder, target, Ct);
        Assert.Single(moved.List());
    }

    [Fact]
    public async Task Copy_library_refuses_a_folder_inside_the_library()
    {
        Directory.CreateDirectory(_library.LibraryFolder);

        await Assert.ThrowsAsync<ArgumentException>(() => SessionLibrary.CopyLibraryAsync(_library.LibraryFolder, Path.Combine(_library.LibraryFolder, "inner"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => SessionLibrary.CopyLibraryAsync(_library.LibraryFolder, _library.LibraryFolder, Ct));
    }

    [Theory]
    [InlineData(@"C:\Users\me\My Drive\Claudette", "Google Drive")]
    [InlineData(@"G:\My Drive\sessions", "Google Drive")]
    [InlineData(@"C:\Users\me\Google Drive\Claudette", "Google Drive")]
    [InlineData("/Users/me/Library/CloudStorage/GoogleDrive-me@example.com/My Drive/lib", "Google Drive")]
    [InlineData(@"C:\Users\me\Dropbox\lib", "Dropbox")]
    [InlineData(@"C:\Users\me\Dropbox (Contoso)\lib", "Dropbox")]
    [InlineData("/Users/me/Library/CloudStorage/Dropbox/lib", "Dropbox")]
    [InlineData(@"C:\Users\me\OneDrive - Contoso\lib", "OneDrive")]
    [InlineData("/Users/me/Library/CloudStorage/OneDrive-Personal/lib", "OneDrive")]
    [InlineData(@"E:\Work Sync\lib", "OneDrive")]
    [InlineData("/Users/me/Library/Mobile Documents/com~apple~CloudDocs/lib", "iCloud Drive")]
    [InlineData(@"C:\Users\me\iCloudDrive\lib", "iCloud Drive")]
    [InlineData(@"C:\Users\me\Box\lib", "Box")]
    [InlineData("/Users/me/Library/CloudStorage/Box-Box/lib", "Box")]
    [InlineData(@"D:\Projects\Box\lib", null)]
    [InlineData(@"C:\Users\me\AppData\Local\Claudette\library", null)]
    [InlineData("/home/me/.local/share/claudette/library", null)]
    public void Detects_cloud_sync_folders(string path, string? expected)
    {
        var environment = new Dictionary<string, string?> { ["OneDriveCommercial"] = @"E:\Work Sync" };

        var detected = SessionLibrary.IsInCloudSyncFolder(path, out var provider, @"C:\Users\me", name => environment.GetValueOrDefault(name));

        Assert.Equal(expected is not null, detected);
        Assert.Equal(expected, provider);
    }

    [Fact]
    public async Task Shared_usage_is_a_file_per_machine_and_a_machine_reads_the_others()
    {
        await _library.WriteSharedUsageAsync("machine1", """{"version":1}""", Ct);
        await _library.WriteSharedUsageAsync("machine2", """{"version":2}""", Ct);
        // A sync client's half-written upload, and something else in the folder.
        await File.WriteAllTextAsync(Path.Combine(_library.UsageFolder, "machine3.json.0123.tmp"), "{", Ct);
        await File.WriteAllTextAsync(Path.Combine(_library.UsageFolder, "notes.txt"), "", Ct);

        Assert.Equal(["machine1.json", "machine2.json", "machine3.json.0123.tmp", "notes.txt"],
            Directory.GetFiles(_library.UsageFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal([("machine2", """{"version":2}""")], _library.ReadSharedUsage("machine1"));
        await Assert.ThrowsAsync<ArgumentException>(() => _library.WriteSharedUsageAsync("../elsewhere", "{}", Ct));
    }

    [Fact]
    public void No_shared_usage_yet_reads_as_none() => Assert.Empty(_library.ReadSharedUsage("machine1"));

    [Theory]
    [InlineData("abc (1).jsonl", "copy 1")]
    [InlineData("abc (DESKTOP-01's conflicted copy 2026-09-28).jsonl", "DESKTOP-01's conflicted copy 2026-09-28")]
    [InlineData("abc-DESKTOP-01.jsonl", "DESKTOP-01")]
    [InlineData("abc.sync-conflict-20260928-101010-ABCDEFG.jsonl", "sync-conflict-20260928-101010-ABCDEFG")]
    [InlineData("abc.jsonl", null)]
    [InlineData("other (1).jsonl", null)]
    public void Conflict_labels(string fileName, string? expected)
    {
        Assert.Equal(expected, SessionLibrary.ConflictLabel("abc", fileName));
    }
}
