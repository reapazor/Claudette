using Claudette.Core.History;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.History;

public sealed class HistoryIndexTests : IDisposable
{
    private readonly TempFolder _root = new("claudette-history");
    private readonly HistoryIndex _index;

    public HistoryIndexTests() => _index = new HistoryIndex(_root.Path);

    public void Dispose() => _root.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WriteTranscript(string project, string sessionId, params string[] lines) =>
        _root.Write($"{project}/{sessionId}.jsonl", string.Join("\n", lines) + "\n");

    private static string Prompt(string text, string time, string cwd = "/work/app", string branch = "main", string version = "2.1.284") =>
        $$"""{"type":"user","isSidechain":false,"message":{"role":"user","content":{{System.Text.Json.JsonSerializer.Serialize(text)}}},"timestamp":"{{time}}","cwd":"{{cwd}}","gitBranch":"{{branch}}","version":"{{version}}"}""";

    private static string AssistantText(string text, string time) =>
        $$"""{"type":"assistant","isSidechain":false,"message":{"role":"assistant","content":[{"type":"text","text":"{{text}}"}]},"timestamp":"{{time}}","cwd":"/work/app"}""";

    [Fact]
    public async Task Summarizes_the_fixture_transcript()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "transcripts", "2.1.284", "tools-and-titles.jsonl");
        var path = _root.Combine("C--repo", "32e03f9e-23d3-48be-8a87-bb153b62c7c2.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(fixture, path);

        var summary = Assert.Single(await _index.ScanAsync(Ct));

        Assert.Equal("32e03f9e-23d3-48be-8a87-bb153b62c7c2", summary.SessionId);
        Assert.Equal(path, summary.TranscriptPath);
        Assert.Equal("<ROOT>\\repo", summary.Folder);
        Assert.Equal("Renamed by host", summary.Title);
        Assert.Equal("WRITE_FILE <ROOT>/repo/b.txt", summary.FirstPrompt);
        // Two prompts and two assistant texts; tool calls and tool results don't count.
        Assert.Equal(4, summary.MessageCount);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T22:37:33.693Z", System.Globalization.CultureInfo.InvariantCulture), summary.LastActivity);
        Assert.Equal("master", summary.GitBranch);
        Assert.Equal("2.1.284", summary.ClaudeCodeVersion);
    }

    [Fact]
    public async Task Reads_prompts_counts_and_folder_from_hand_written_lines()
    {
        WriteTranscript("p", "s1",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"meta"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/work/first"}""",
            """{"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>"},"timestamp":"2026-01-01T00:00:01Z"}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model</local-command-stdout>"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Ask clarifying questions."},{"type":"text","text":"<command-name>/compact</command-name>"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"<system-reminder>context</system-reminder>"}]}}""",
            "not json at all",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"a subagent prompt"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Fix the login bug\n<system-reminder>injected</system-reminder>"}]},"timestamp":"2026-01-01T00:01:00Z","cwd":"/work/second","gitBranch":"feature/a","version":"2.1.0"}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]},"timestamp":"2026-01-01T00:01:05Z"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file text"}]},"timestamp":"2026-01-01T00:01:06Z"}""",
            AssistantText("Fixed it.", "2026-01-01T00:01:10Z"),
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"   "}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""",
            """{"type":"ai-title","aiTitle":"First title"}""",
            Prompt("and the logout one", "2026-01-01T00:02:00Z", cwd: "/work/third", branch: "feature/b", version: "2.1.1"),
            """{"type":"ai-title","aiTitle":"Second title"}""",
            """{"type":"system","subtype":"x","timestamp":"2026-01-01T00:03:00Z"}""");

        var summary = Assert.Single(await _index.ScanAsync(Ct));

        Assert.Equal("/work/first", summary.Folder);
        Assert.Equal("Fix the login bug", summary.FirstPrompt);
        Assert.Equal(3, summary.MessageCount);
        Assert.Equal("Second title", summary.Title);
        Assert.Equal("feature/b", summary.GitBranch);
        Assert.Equal("2.1.1", summary.ClaudeCodeVersion);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 3, 0, TimeSpan.Zero), summary.LastActivity);
    }

    [Fact]
    public async Task A_custom_title_beats_ai_titles_and_the_last_one_wins()
    {
        WriteTranscript("p", "s1",
            Prompt("hello", "2026-01-01T00:00:00Z"),
            """{"type":"custom-title","customTitle":"Mine"}""",
            """{"type":"custom-title","customTitle":"Mine, renamed"}""",
            """{"type":"ai-title","aiTitle":"Generated later"}""");

        Assert.Equal("Mine, renamed", Assert.Single(await _index.ScanAsync(Ct)).Title);
    }

    [Fact]
    public async Task A_long_first_prompt_is_cut_to_one_short_line()
    {
        WriteTranscript("p", "s1", Prompt("line one\n\n" + new string('x', 500), "2026-01-01T00:00:00Z"));

        var prompt = Assert.Single(await _index.ScanAsync(Ct)).FirstPrompt!;

        Assert.StartsWith("line one xxx", prompt, StringComparison.Ordinal);
        Assert.EndsWith("…", prompt, StringComparison.Ordinal);
        Assert.Equal(HistoryIndex.FirstPromptLength + 1, prompt.Length);
    }

    [Fact]
    public async Task Without_timestamps_last_activity_is_the_file_time()
    {
        var path = WriteTranscript("p", "s1", """{"type":"ai-title","aiTitle":"Only a title"}""");
        var written = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        var summary = Assert.Single(await _index.ScanAsync(Ct));

        Assert.Equal(new DateTimeOffset(written), summary.LastActivity);
        Assert.Null(summary.FirstPrompt);
        Assert.Equal(0, summary.MessageCount);
    }

    [Fact]
    public async Task Sessions_without_a_prompt_or_title_are_skipped()
    {
        WriteTranscript("p", "empty");
        WriteTranscript("p", "meta-only",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"meta"},"timestamp":"2026-01-01T00:00:00Z"}""",
            """{"type":"queue-operation","operation":"enqueue","timestamp":"2026-01-01T00:00:00Z"}""");

        Assert.Empty(await _index.ScanAsync(Ct));
    }

    [Fact]
    public async Task Subagent_transcripts_and_other_files_are_ignored()
    {
        WriteTranscript("p", "main", Prompt("top level", "2026-01-01T00:00:00Z"));
        _root.Write("p/main/subagents/agent-1.jsonl", Prompt("subagent", "2026-01-02T00:00:00Z"));
        _root.Write("p/notes.txt", Prompt("not a transcript", "2026-01-02T00:00:00Z"));
        _root.Write("stray.jsonl", Prompt("not in a project folder", "2026-01-02T00:00:00Z"));

        Assert.Equal("main", Assert.Single(await _index.ScanAsync(Ct)).SessionId);
    }

    [Fact]
    public async Task Sessions_come_newest_first_across_projects()
    {
        WriteTranscript("a", "old", Prompt("old", "2026-01-01T00:00:00Z"));
        WriteTranscript("b", "new", Prompt("new", "2026-02-01T00:00:00Z"));
        WriteTranscript("a", "middle", Prompt("middle", "2026-01-15T00:00:00Z"));

        Assert.Equal(["new", "middle", "old"], (await _index.ScanAsync(Ct)).Select(s => s.SessionId));
    }

    [Fact]
    public async Task An_unchanged_file_is_not_read_again()
    {
        var path = WriteTranscript("p", "s1", Prompt("alpha", "2026-01-01T00:00:00Z"));
        var written = File.GetLastWriteTimeUtc(path);
        Assert.Equal("alpha", Assert.Single(await _index.ScanAsync(Ct)).FirstPrompt);

        // Same length and last-write time: the cached summary is used.
        File.WriteAllText(path, File.ReadAllText(path).Replace("alpha", "bravo", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal("alpha", Assert.Single(await _index.ScanAsync(Ct)).FirstPrompt);

        File.SetLastWriteTimeUtc(path, written.AddSeconds(5));
        Assert.Equal("bravo", Assert.Single(await _index.ScanAsync(Ct)).FirstPrompt);
    }

    [Fact]
    public async Task A_transcript_that_grew_is_read_on_from_where_the_last_scan_stopped()
    {
        var path = WriteTranscript("p", "s1", Prompt("alpha", "2026-01-01T00:00:00Z"), AssistantText("ok", "2026-01-01T00:00:01Z"));
        Assert.Equal(2, Assert.Single(await _index.ScanAsync(Ct)).MessageCount);

        // Proof it isn't read again from the start: an earlier line changed in place is left as it was read.
        File.WriteAllText(path, File.ReadAllText(path).Replace("alpha", "bravo", StringComparison.Ordinal)
            + Prompt("next", "2026-01-02T00:00:00Z") + "\n");
        var grown = Assert.Single(await _index.ScanAsync(Ct));

        Assert.Equal(("alpha", 3), (grown.FirstPrompt, grown.MessageCount));
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), grown.LastActivity);
        Assert.Contains("next", grown.Prompts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transcript_rewritten_longer_is_read_again_from_the_start()
    {
        var path = WriteTranscript("p", "s1", Prompt("alpha", "2026-01-01T00:00:00Z"));
        Assert.Single(await _index.ScanAsync(Ct));

        // Its last line is different now: not only appended to.
        WriteTranscript("p", "s1", Prompt("something else entirely", "2026-01-03T00:00:00Z"), Prompt("more", "2026-01-03T00:00:01Z"));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

        var summary = Assert.Single(await _index.ScanAsync(Ct));
        Assert.Equal(("something else entirely", 2), (summary.FirstPrompt, summary.MessageCount));
    }

    [Fact]
    public async Task A_last_line_still_being_written_is_read_whole_once_it_is()
    {
        var path = WriteTranscript("p", "s1", Prompt("alpha", "2026-01-01T00:00:00Z"));
        File.AppendAllText(path, Prompt("half", "2026-01-02T00:00:00Z")[..40]);
        Assert.Equal(1, Assert.Single(await _index.ScanAsync(Ct)).MessageCount);

        File.WriteAllText(path, File.ReadAllText(path)[..^40] + Prompt("half", "2026-01-02T00:00:00Z") + "\n");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        Assert.Equal(2, Assert.Single(await _index.ScanAsync(Ct)).MessageCount);
    }

    [Fact]
    public async Task Transcripts_elsewhere_are_summarized_and_cached_too()
    {
        var library = _root.Write("library/s9.jsonl", Prompt("from the library", "2026-01-01T00:00:00Z") + "\n");
        var written = File.GetLastWriteTimeUtc(library);
        Assert.Equal("from the library", (await _index.SummarizeAsync([library], Ct))[library]!.FirstPrompt);

        File.WriteAllText(library, File.ReadAllText(library).Replace("library", "LIBRARY", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(library, written);
        Assert.Equal("from the library", (await _index.SummarizeAsync([library], Ct))[library]!.FirstPrompt);
        Assert.Null((await _index.SummarizeAsync([_root.Combine("library", "gone.jsonl")], Ct)).Values.Single());
    }

    [Fact]
    public async Task Deleted_files_drop_out_and_new_ones_appear()
    {
        var first = WriteTranscript("p", "s1", Prompt("one", "2026-01-01T00:00:00Z"));
        Assert.Single(await _index.ScanAsync(Ct));

        File.Delete(first);
        WriteTranscript("p", "s2", Prompt("two", "2026-01-01T00:00:00Z"));

        Assert.Equal("s2", Assert.Single(await _index.ScanAsync(Ct)).SessionId);
    }

    [Fact]
    public async Task An_unreadable_file_does_not_fail_the_scan()
    {
        WriteTranscript("p", "fine", Prompt("fine", "2026-01-02T00:00:00Z"));
        var locked = WriteTranscript("p", "locked", Prompt("locked", "2026-01-01T00:00:00Z"));

        IReadOnlyList<SessionSummary> sessions;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            sessions = await _index.ScanAsync(Ct);
        }

        Assert.Contains(sessions, s => s.SessionId == "fine");
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(sessions, s => s.SessionId == "locked");
        }
        // A failed read isn't cached, so it's tried again.
        Assert.Contains(await _index.ScanAsync(Ct), s => s.SessionId == "locked");
    }

    [Fact]
    public async Task Concurrent_scans_are_safe()
    {
        for (var i = 0; i < 20; i++)
        {
            WriteTranscript("p", $"s{i:00}", Prompt($"prompt {i}", $"2026-01-01T00:00:{i:00}Z"));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _index.ScanAsync(Ct)));

        Assert.All(results, r => Assert.Equal(20, r.Count));
    }

    [Fact]
    public async Task A_missing_projects_folder_is_empty()
    {
        Assert.Empty(await new HistoryIndex(_root.Combine("nope")).ScanAsync(Ct));
    }

    [Fact]
    public void Filter_needs_every_word_in_the_title_or_first_prompt()
    {
        SessionSummary Session(string id, string? title, string? prompt) =>
            new(id, id, null, title, prompt, 1, DateTimeOffset.UnixEpoch, null, null);
        var sessions = new[]
        {
            Session("a", "Login bug", "Fix the redirect after sign-in"),
            Session("b", null, "Refactor the LOGIN form"),
            Session("c", "Docs", "Write the README"),
        };

        Assert.Equal(["a", "b"], HistoryIndex.Filter(sessions, "login").Select(s => s.SessionId));
        Assert.Equal(["a"], HistoryIndex.Filter(sessions, "LOGIN redirect").Select(s => s.SessionId));
        Assert.Empty(HistoryIndex.Filter(sessions, "login readme"));
        Assert.Equal(3, HistoryIndex.Filter(sessions, "  ").Count);
    }

    [Fact]
    public async Task Search_looks_through_every_prompt_not_just_the_first()
    {
        WriteTranscript("p", "s1",
            Prompt("Fix the login bug", "2026-01-01T00:00:00Z"),
            AssistantText("Done", "2026-01-01T00:00:10Z"),
            Prompt("Now   add a\nregression test for the redirect", "2026-01-01T00:01:00Z"));

        var summary = Assert.Single(await _index.ScanAsync(Ct));

        Assert.Equal("Fix the login bug\nNow add a regression test for the redirect\n", summary.Prompts);
        Assert.Single(HistoryIndex.Filter([summary], "regression redirect"));
        Assert.Empty(HistoryIndex.Filter([summary], "regression readme"));
    }
}
