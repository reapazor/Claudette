using System.Diagnostics;
using System.Text.Json.Nodes;
using Claudette.Core.Diffs;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.IntegrationTests.Support;
using Claudette.MockApi;

namespace Claudette.IntegrationTests;

/// <summary>
/// The real <c>claude</c> against a mock Messages API: real tools, prompts, edits and transcripts, and no tokens
/// (DESIGN.md §15). Every test gets its own <c>CLAUDE_CONFIG_DIR</c>, so the user's <c>~/.claude</c> is never touched.
/// Skipped when Claude Code isn't installed.
/// </summary>
[Trait("Category", "RealCli")]
public sealed class RealCliTests : IAsyncLifetime
{
    private readonly TempFolder _root = new("claudette-realcli");
    private MockAnthropicApi _api = null!;
    private ClaudeSessionFactory? _factory;

    private string Work => _root.Combine("repo");

    private string Config => _root.Combine("config");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Config);
        Process.Start(new ProcessStartInfo("git", ["init", "-q"]) { WorkingDirectory = Work, CreateNoWindow = true })?.WaitForExit(10_000);
        _api = await MockAnthropicApi.StartAsync();
        var located = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(null);
        if (located.IsUsable)
        {
            _factory = new ClaudeSessionFactory(located.Install!.Path, new ProcessLauncher(), TimeProvider.System);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        _root.Dispose();
    }

    [Fact]
    public async Task A_turn_round_trips_through_the_real_cli()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("pong", done.Result.Result);
        Assert.Contains(seen, e => e is TextDelta);
        Assert.Contains("interrupt_receipt_v1", session.Capabilities);
        Assert.Contains(_api.Requests, r => r.LastUserText.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_nonessential_traffic_no_title_is_generated_and_a_rename_reaches_the_transcript()
    {
        await using var session = await StartAsync();
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        // generate_session_title: with nonessential traffic off, as every RealCli test runs, Claude Code answers with no
        // title and makes no model call; the tab then names itself from the prompt (DESIGN.md §13, "Session naming").
        var requests = _api.Requests.Count;
        var title = await session.GenerateSessionTitleAsync("hello", persist: false, TestContext.Current.CancellationToken);
        Assert.Null(title);
        Assert.Equal(requests, _api.Requests.Count);

        // rename_session: saved in the transcript, where claude --resume and History find it.
        await session.RenameSessionAsync("Renamed by Claudette", TestContext.Current.CancellationToken);
        var transcript = Directory.EnumerateFiles(Path.Combine(Config, "projects"), $"{done.Result.SessionId}.jsonl", SearchOption.AllDirectories).Single();
        await Waiting.UntilAsync(() => File.ReadAllText(transcript).Contains("\"customTitle\":\"Renamed by Claudette\"", StringComparison.Ordinal), "the name in the transcript");
    }

    // ---- Rewind and branch, hook rows, MCP servers (DESIGN.md §5, §4) ------------------------------------------------

    [Fact]
    public async Task A_typed_prompt_comes_back_with_the_id_and_origin_it_was_sent_with()
    {
        await using var session = await StartAsync(replayUserMessages: true);
        var stamp = new MessageStamp(Guid.NewGuid().ToString(), FromUser: true);

        await session.SendUserMessageAsync("hello", [], null, stamp, TestContext.Current.CancellationToken);
        var (replayed, _) = await session.ReadUntilAsync<PromptReplayed>();
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        // DESIGN.md §13: Claude Code keeps the id, and counts the prompt as a person's (checked with 2.1.286).
        Assert.Equal(stamp.Uuid, replayed.Message.Uuid);
        Assert.Equal("human", replayed.Message.Raw["origin"]?["kind"]?.GetValue<string>());
        Assert.Equal(stamp.Uuid, done.Result.Raw["user_message_uuid"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_prompt_comes_back_with_its_uuid_and_files_rewind_to_it()
    {
        await using var session = await StartAsync(replayUserMessages: true,
            environment: new() { ["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true" });
        var file = Path.Combine(Work, "rewound.txt");

        await session.SendUserMessageAsync($"WRITE_FILE {file}", TestContext.Current.CancellationToken);
        var (replayed, _) = await session.ReadUntilAsync<PromptReplayed>();
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Allow();
        await session.ReadUntilAsync<TurnCompleted>();
        Assert.True(File.Exists(file));
        Assert.False(string.IsNullOrEmpty(replayed.Message.Uuid));

        var preview = await session.RewindFilesAsync(replayed.Message.Uuid!, dryRun: true, TestContext.Current.CancellationToken);
        Assert.True(preview.CanRewind, preview.Error);
        Assert.Contains(preview.FilesChanged, f => f.EndsWith("rewound.txt", StringComparison.Ordinal));
        Assert.True(File.Exists(file));

        var rewound = await session.RewindFilesAsync(replayed.Message.Uuid!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rewound.CanRewind, rewound.Error);
        // It didn't exist before that prompt.
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_resume_at_a_reply_drops_the_turns_after_it()
    {
        string sessionId;
        string firstReply;
        await using (var session = await StartAsync())
        {
            await session.SendUserMessageAsync("hello one", TestContext.Current.CancellationToken);
            var (first, seen) = await session.ReadUntilAsync<TurnCompleted>();
            firstReply = seen.OfType<AssistantMessageReceived>().Last().Message.Raw.GetString("uuid")!;
            await session.SendUserMessageAsync("hello two", TestContext.Current.CancellationToken);
            await session.ReadUntilAsync<TurnCompleted>();
            sessionId = first.Result.SessionId!;
        }

        await using var branch = await StartAsync(resume: sessionId, forkSession: true, resumeSessionAt: firstReply);
        await branch.SendUserMessageAsync("hello three", TestContext.Current.CancellationToken);
        await branch.ReadUntilAsync<TurnCompleted>();

        // The model sees the first turn and the new prompt, not the second turn.
        var last = _api.Requests.Last(r => r.Path == "/v1/messages" && r.LastUserText.Contains("hello three", StringComparison.Ordinal));
        Assert.Equal(3, last.MessageCount);
    }

    [Fact]
    public async Task Hook_runs_come_through_as_system_messages()
    {
        Directory.CreateDirectory(Path.Combine(Work, ".claude"));
        await File.WriteAllTextAsync(Path.Combine(Work, ".claude", "settings.json"), """
            {"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"echo checked"}]}]}}
            """, TestContext.Current.CancellationToken);
        await using var session = await StartAsync(includeHookEvents: true);

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var notices = seen.OfType<SystemNotice>().Select(n => n.Message).ToArray();
        var started = Assert.Single(notices, m => m.Subtype == "hook_started" && m.Raw.GetString("hook_event") == "UserPromptSubmit");
        var response = Assert.Single(notices, m => m.Subtype == "hook_response" && m.Raw.GetString("hook_event") == "UserPromptSubmit");
        Assert.Equal(started.Raw.GetString("hook_id"), response.Raw.GetString("hook_id"));
        Assert.Equal("success", response.Raw.GetString("outcome"));
        Assert.Contains("checked", response.Raw.GetString("stdout") ?? response.Raw.GetString("output"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_servers_report_their_state_and_can_be_turned_off()
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["broken"] = new JsonObject { ["type"] = "stdio", ["command"] = "false" } },
        }.ToJsonString();
        await using var session = await StartAsync(additionalArguments: ["--mcp-config", config, "--strict-mcp-config"]);

        McpServerStatus? broken = null;
        await Waiting.UntilAsync(() =>
        {
            broken = session.GetMcpStatusAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult().SingleOrDefault(s => s.Name == "broken");
            return broken is { State: not McpServerState.Pending };
        }, "the server to fail", TimeSpan.FromSeconds(30));
        Assert.Equal(McpServerState.Failed, broken!.State);

        await session.SetMcpServerEnabledAsync("broken", false, TestContext.Current.CancellationToken);
        var after = Assert.Single(await session.GetMcpStatusAsync(TestContext.Current.CancellationToken), s => s.Name == "broken");
        Assert.Equal(McpServerState.Disabled, after.State);
    }

    [Fact]
    public async Task A_fallback_model_is_accepted()
    {
        await using var session = await StartAsync(fallbackModel: "claude-sonnet-5-5");

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("pong", done.Result.Result);
    }

    [Fact]
    public async Task An_allowed_write_creates_the_file()
    {
        await using var session = await StartAsync();
        var file = Path.Combine(Work, "written.txt");

        await session.SendUserMessageAsync($"WRITE_FILE {file}", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Allow();
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("Write", requested.Request.ToolName);
        Assert.Equal("Done with the tool.", done.Result.Result);
        Assert.Equal("hello from mock\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        var result = seen.OfType<ToolResultsReceived>().Single().Message.ToolUseResult!;
        Assert.Equal("create", result["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_denied_write_leaves_the_file_alone()
    {
        await using var session = await StartAsync();
        var file = Path.Combine(Work, "denied.txt");

        await session.SendUserMessageAsync($"WRITE_FILE {file}", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Deny("Not now");
        await session.ReadUntilAsync<TurnCompleted>();

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Always_allow_saves_the_rule_to_project_settings()
    {
        await using var session = await StartAsync();
        var rule = new JsonArray(new JsonObject
        {
            ["type"] = "addRules",
            ["rules"] = new JsonArray(new JsonObject { ["toolName"] = "Bash", ["ruleContent"] = "touch:*" }),
            ["behavior"] = "allow",
            ["destination"] = "localSettings",
        });

        await session.SendUserMessageAsync("RUN_BASH touch first.txt", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        Assert.Contains(requested.Request.Suggestions, s => s["type"]?.GetValue<string>() == "addRules");
        requested.Request.Allow(updatedPermissions: rule);
        await session.ReadUntilAsync<TurnCompleted>();

        var settings = await File.ReadAllTextAsync(Path.Combine(Work, ".claude", "settings.local.json"), TestContext.Current.CancellationToken);
        Assert.Contains("Bash(touch:*)", settings, StringComparison.Ordinal);

        await session.SendUserMessageAsync("RUN_BASH touch second.txt", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();
        Assert.DoesNotContain(seen, e => e is PermissionRequested);
    }

    [Fact]
    public async Task A_clarifying_question_is_answered_through_the_permission_reply()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("ASK_QUESTION", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        Assert.Equal("AskUserQuestion", requested.Request.ToolName);
        var input = (JsonObject)requested.Request.Input.DeepClone();
        input["answers"] = new JsonObject { ["Which database?"] = "SQLite" };
        requested.Request.Allow(input);
        await session.ReadUntilAsync<TurnCompleted>();

        // The answer reaches the model as the tool's result (DESIGN.md §7).
        Assert.Contains(_api.Requests, r => r.LastToolResultText.Contains("SQLite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approving_a_plan_leaves_plan_mode()
    {
        await using var session = await StartAsync(permissionMode: "plan");

        await session.SendUserMessageAsync("EXIT_PLAN", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        Assert.Equal("ExitPlanMode", requested.Request.ToolName);
        Assert.Equal("1. Read the code\n2. Fix the bug", requested.Request.Input["plan"]?.GetValue<string>());
        requested.Request.AllowAndSetMode("acceptEdits");
        await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("acceptEdits", session.PermissionMode);
    }

    [Fact]
    public async Task Auto_mode_starts_with_a_model_that_supports_it_and_falls_back_to_manual_otherwise()
    {
        // What a tab relies on when it starts in auto mode (DESIGN.md §7, "Starting mode").
        await using (var sonnet = await StartAsync(permissionMode: "auto", model: "sonnet"))
        {
            Assert.Equal("auto", sonnet.PermissionMode);
            Assert.Contains(sonnet.Initialization!.Models, m => m.SupportsAutoMode);
        }

        await using var haiku = await StartAsync(permissionMode: "auto");

        Assert.Equal("default", haiku.PermissionMode);
        Assert.Contains(haiku.Initialization!.Models, m => m.ResolvedModel?.StartsWith("claude-haiku", StringComparison.Ordinal) == true && !m.SupportsAutoMode);
        await Assert.ThrowsAnyAsync<Exception>(() => haiku.SetPermissionModeAsync("auto", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_edit_reports_the_original_file()
    {
        await using var session = await StartAsync(permissionMode: "acceptEdits");
        var file = Path.Combine(Work, "edit.txt");
        await File.WriteAllTextAsync(file, "first line\nORIGINAL LINE\nlast line\n", TestContext.Current.CancellationToken);

        await session.SendUserMessageAsync($"EDIT_FILE {file}", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var edit = seen.OfType<ToolResultsReceived>().Select(r => r.Message.ToolUseResult).Single(r => r?["oldString"] is not null)!;
        Assert.Equal("first line\nORIGINAL LINE\nlast line\n", edit["originalFile"]!.GetValue<string>());
        Assert.Contains("EDITED LINE", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// Claude Code sends a large file's whole <c>originalFile</c> live, but writes it to the transcript as null over
    /// 10,000 characters; the changed files keep it for the replay (DESIGN.md §8, "Before content").
    /// </summary>
    [Fact]
    public async Task A_large_original_file_comes_live_but_not_in_the_transcript_so_it_is_kept_for_replays()
    {
        var file = Path.Combine(Work, "large.txt");
        var original = string.Concat(Enumerable.Range(1, 400).Select(i => $"\tfiller line {i:D4}, to take the file past 10,000 characters\n"))
            + "ORIGINAL LINE\nlast line\n";
        await File.WriteAllTextAsync(file, original, TestContext.Current.CancellationToken);
        var befores = new BeforeContentStore(_root.Combine("before-content"), TimeProvider.System);
        await using var session = await StartAsync(permissionMode: "acceptEdits");

        await session.SendUserMessageAsync($"EDIT_FILE {file}", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var live = seen.Select(e => e switch
        {
            AssistantMessageReceived a => (Core.Protocol.ClaudeMessage)a.Message,
            ToolResultsReceived r => r.Message,
            _ => null,
        }).OfType<Core.Protocol.ClaudeMessage>().ToArray();
        var liveEdit = live.OfType<Core.Protocol.UserMessage>().Select(r => r.ToolUseResult).OfType<JsonObject>().Single(r => r["oldString"] is not null);
        Assert.Equal(original, liveEdit["originalFile"]!.GetValue<string>());
        var liveChanges = Replay(new ChangedFiles(TimeProvider.System) { Befores = befores }, live);
        Assert.Equal(original, Assert.Single(liveChanges.Files).Before);

        var transcript = await Core.Transcripts.TranscriptReader.ReadAsync(
            Directory.EnumerateFiles(Path.Combine(Config, "projects"), "*.jsonl", SearchOption.AllDirectories).Single(), TestContext.Current.CancellationToken);
        var saved = transcript.Items.OfType<Core.Transcripts.TranscriptMessage>().Select(m => m.Message).ToArray();
        var savedEdit = saved.OfType<Core.Protocol.UserMessage>().Select(r => r.ToolUseResult).OfType<JsonObject>().Single(r => r["oldString"] is not null);
        Assert.True(savedEdit.ContainsKey("originalFile"));
        Assert.Null(savedEdit["originalFile"]);
        var replayed = Assert.Single(Replay(new ChangedFiles(TimeProvider.System) { Befores = befores }, saved).Files);
        Assert.True(replayed.BeforeKnown);
        Assert.Equal(original, replayed.Before);

        static ChangedFiles Replay(ChangedFiles files, IEnumerable<Core.Protocol.ClaudeMessage> messages)
        {
            foreach (var message in messages)
            {
                if (message is Core.Protocol.AssistantMessage assistant)
                {
                    foreach (var use in assistant.Content.OfType<Core.Protocol.ToolUseBlock>())
                    {
                        files.RecordToolUse(use.Id, use.Name, use.Input);
                    }
                }
                else if (message is Core.Protocol.UserMessage user)
                {
                    foreach (var result in user.Content.OfType<Core.Protocol.ToolResultBlock>())
                    {
                        files.RecordToolResult(result.ToolUseId, result.IsError, user.ToolUseResult);
                    }
                }
            }
            return files;
        }
    }

    [Fact]
    public async Task Model_and_effort_changes_reach_the_api()
    {
        await using var session = await StartAsync();

        await session.SetModelAsync("sonnet", TestContext.Current.CancellationToken);
        await session.SetEffortAsync("low", TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TurnCompleted>();

        var request = _api.Requests.Last(r => r.Reply == "text");
        Assert.Contains("sonnet", request.Model, StringComparison.Ordinal);
        Assert.Equal("low", request.Effort);
    }

    [Fact]
    public async Task A_refused_start_says_why()
    {
        // DESIGN.md §4, "Why it couldn't start": every claude starts with CLAUDE_CODE_STARTUP_FAILURE_RESULTS=1 (checked with 2.1.286).
        var ex = await Assert.ThrowsAsync<ClaudeSessionExitedException>(() => StartAsync(environment: new() { ["HTTPS_PROXY"] = "not-a-url", ["https_proxy"] = "not-a-url" }));

        Assert.Equal("proxy_invalid", ex.StartupFailure?.Reason);
        Assert.Contains("https_proxy", ex.StartupFailure!.Errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interrupt_stops_a_streaming_reply()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("SLOW", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TextDelta>();
        await session.InterruptAsync(TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(15));

        Assert.Equal("aborted_streaming", done.Result.TerminalReason);
    }

    [Fact]
    public async Task Messages_waiting_their_turn_are_taken_back_one_by_one_or_with_the_interrupt()
    {
        await using var session = await StartAsync(replayUserMessages: true);
        var ct = TestContext.Current.CancellationToken;
        string Id() => Guid.NewGuid().ToString();

        // DESIGN.md §5, "Queued messages" (checked with 2.1.286): cancel_async_message takes one back...
        await session.SendUserMessageAsync("SLOW", [], null, new MessageStamp(Id(), FromUser: true), ct);
        await session.ReadUntilAsync<TextDelta>();
        var one = Id();
        await session.SendUserMessageAsync("one", [], null, new MessageStamp(one, FromUser: true), ct);
        Assert.True(await session.CancelQueuedMessageAsync(one, ct));
        Assert.False(await session.CancelQueuedMessageAsync(Id(), ct));

        // ...and an interrupt with cancel_queued takes back the rest, which never run.
        var two = Id();
        await session.SendUserMessageAsync("two", [], null, new MessageStamp(two, FromUser: true), ct);
        var receipt = await session.InterruptAsync(cancelQueued: true, ct);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(15));

        Assert.Contains(ClaudeSession.InterruptCancelQueuedCapability, session.Capabilities);
        Assert.Equal([two], receipt.Cancelled);
        Assert.Empty(receipt.StillQueued);
        Assert.Equal("aborted_streaming", done.Result.TerminalReason);
        Assert.DoesNotContain(seen.OfType<PromptReplayed>(), p => p.Message.Uuid == one || p.Message.Uuid == two);
    }

    [Fact]
    public async Task An_attached_image_reaches_the_api()
    {
        await using var session = await StartAsync();
        // A 1×1 PNG: small enough that Claude Code passes it on as it is.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        await session.SendUserMessageAsync("hello, what is in this image?", [new Core.Protocol.MessageImage("image/png", png)], TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.False(done.Result.IsError);
        var request = _api.Requests.Last(r => r.Reply == "text");
        var image = Assert.Single(request.LastUserImages!);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(1, image.Width);
        Assert.Contains("what is in this image?", request.LastUserText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_with_no_text_is_a_message_too()
    {
        await using var session = await StartAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        // A pasted screenshot sent on its own (DESIGN.md §5, "Attachments"): only image blocks, no text block.
        await session.SendUserMessageAsync("", [new Core.Protocol.MessageImage("image/png", png)], TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.False(done.Result.IsError);
        Assert.Equal("pong", done.Result.Result);
        var request = _api.Requests.Last(r => r.Reply == "text");
        Assert.Equal("image/png", Assert.Single(request.LastUserImages!).MediaType);
    }

    [Fact]
    public async Task Claude_code_scales_a_large_image_down_itself()
    {
        await using var session = await StartAsync();

        // Claudette relies on this rather than resizing images itself (DESIGN.md §5, "Attachments").
        await session.SendUserMessageAsync("hello, a big one", [new Core.Protocol.MessageImage("image/png", Gradient(3000, 2000))], TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TurnCompleted>();

        var image = Assert.Single(_api.Requests.Last(r => r.Reply == "text").LastUserImages!);
        Assert.True(image.Width <= 2000 && image.Height <= 2000, $"Expected at most 2000 px, got {image.Width}×{image.Height}.");
    }

    [Fact]
    public async Task Claude_code_brings_a_large_file_under_the_api_size_limit()
    {
        await using var session = await StartAsync();
        // About 12 MB and within 2000 px, so only its size is too big for the API (5 MB an image).
        var png = Gradient(2000, 2000, noisy: true);
        Assert.True(png.Length > 10 * 1024 * 1024, $"Expected over 10 MB, got {png.Length} bytes.");

        // The 20 MB Claudette allows relies on this (DESIGN.md §5, "Attachments").
        await session.SendUserMessageAsync("hello, a heavy one", [new Core.Protocol.MessageImage("image/png", png)], TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.False(done.Result.IsError);
        var image = Assert.Single(_api.Requests.Last(r => r.Reply == "text").LastUserImages!);
        Assert.True(image.Bytes * 4L / 3 <= 5 * 1024 * 1024, $"Expected at most 5 MB as base64, got {image.Bytes} bytes ({image.MediaType}).");
    }

    [Fact]
    public async Task An_at_mention_is_read_by_claude_code()
    {
        await using var session = await StartAsync();
        await File.WriteAllTextAsync(Path.Combine(Work, "mentioned.txt"), "MENTIONED FILE CONTENT\n", TestContext.Current.CancellationToken);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        // With an image too: Claude Code only expands mentions in the last block, so the text must come after it.
        await session.SendUserMessageAsync("hello, see @mentioned.txt", [new Core.Protocol.MessageImage("image/png", png)], TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TurnCompleted>();

        var request = _api.Requests.Last(r => r.Reply == "text");
        Assert.Contains("MENTIONED FILE CONTENT", request.LastUserText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_and_init_list_the_slash_commands()
    {
        Directory.CreateDirectory(Path.Combine(Work, ".claude", "commands"));
        await File.WriteAllTextAsync(Path.Combine(Work, ".claude", "commands", "ship-it.md"), "---\ndescription: Ship the change\nargument-hint: <version>\n---\nShip $ARGUMENTS\n", TestContext.Current.CancellationToken);
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (started, _) = await session.ReadUntilAsync<TurnStarted>();
        await session.ReadUntilAsync<TurnCompleted>();

        var command = Assert.Single(session.Initialization!.Commands, c => c.Name == "ship-it");
        Assert.StartsWith("Ship the change", command.Description, StringComparison.Ordinal);
        Assert.Equal("<version>", command.ArgumentHint);
        Assert.Contains(session.Initialization.Commands, c => c is { Name: "compact", IsBuiltIn: true });
        Assert.Contains("ship-it", started.Init.SlashCommands);
    }

    [Fact]
    public async Task A_suffix_beside_a_slash_command_reaches_its_prompt_and_not_its_arguments()
    {
        Directory.CreateDirectory(Path.Combine(Work, ".claude", "commands"));
        await File.WriteAllTextAsync(Path.Combine(Work, ".claude", "commands", "ship-it.md"), "Ship version $ARGUMENTS now.\n", TestContext.Current.CancellationToken);
        await using var session = await StartAsync();

        // As a tab sends a command with a quick suffix (DESIGN.md §5, "Quick suffixes"): in a block before the command.
        await session.SendUserMessageAsync("/ship-it 1.2", [], "Ask clarifying questions first.", TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.False(done.Result.IsError);
        var text = _api.Requests.Last(r => r.Reply == "text").LastUserText;
        Assert.Contains("Ship version 1.2 now.", text, StringComparison.Ordinal);
        Assert.Contains("Ask clarifying questions first.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_usage_is_reported()
    {
        await using var session = await StartAsync();

        var usage = await session.GetContextUsageAsync(TestContext.Current.CancellationToken);

        Assert.True(usage.MaxTokens > 0);
    }

    [Fact]
    public async Task Resuming_from_a_transcript_file_continues_next_to_it()
    {
        string sessionId;
        await using (var first = await StartAsync())
        {
            await first.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
            var (done, _) = await first.ReadUntilAsync<TurnCompleted>();
            sessionId = done.Result.SessionId!;
        }
        var transcript = Directory.EnumerateFiles(Path.Combine(Config, "projects"), $"{sessionId}.jsonl", SearchOption.AllDirectories).Single();
        var library = _root.Combine("library");
        Directory.CreateDirectory(library);
        var copy = Path.Combine(library, "copied.jsonl");
        File.Copy(transcript, copy);

        await using var resumed = await StartAsync(resume: copy);
        await resumed.SendUserMessageAsync("hello again", TestContext.Current.CancellationToken);
        await resumed.ReadUntilAsync<TurnCompleted>();

        Assert.Equal(sessionId, resumed.SessionId);
        Assert.True(File.Exists(Path.Combine(library, $"{sessionId}.jsonl")), "The continued transcript should be written next to the resumed file (DESIGN.md §9).");
    }

    /// <summary>
    /// An RGB PNG with a gradient, which compresses well, so a big one stays small. With <paramref name="noisy"/> each
    /// pixel is random instead, so it barely compresses, like a large photo-like screenshot.
    /// </summary>
    private static byte[] Gradient(int width, int height, bool noisy = false)
    {
        var rows = new byte[(width * 3 + 1) * height];
        var random = new Random(1);
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
            if (noisy)
            {
                random.NextBytes(rows.AsSpan(row + 1, width * 3));
                continue;
            }
            for (var x = 0; x < width; x++)
            {
                rows[row + 1 + x * 3] = (byte)x;
                rows[row + 2 + x * 3] = (byte)y;
                rows[row + 3 + x * 3] = 128;
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest))
        {
            zlib.Write(rows);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            stream.Write(typed);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            stream.Write(crc);
        }

        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }
            return ~crc;
        }
    }

    /// <summary>
    /// The PreToolUse hook Perforce handling registers (DESIGN.md §18): Claude Code calls it back before the Bash
    /// command, waits for the answer, and then asks for permission as usual; the note goes in with
    /// --append-system-prompt.
    /// </summary>
    [Fact]
    public async Task A_PreToolUse_hook_is_called_back_before_Bash_runs()
    {
        var calls = new List<(HookInput Input, DateTimeOffset At)>();
        var hook = new HookRegistration("PreToolUse", "Bash", async (input, _) =>
        {
            lock (calls)
            {
                calls.Add((input, DateTimeOffset.UtcNow));
            }
            // Holds the command back, as a login would.
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            return HookOutputs.Continue();
        }, TimeSpan.FromMinutes(5));
        await using var session = await StartAsync(hooks: [hook], appendSystemPrompt: "This folder is in a Perforce workspace.");

        await session.SendUserMessageAsync("RUN_BASH touch hooked.txt", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        var askedAt = DateTimeOffset.UtcNow;
        requested.Request.Allow();
        await session.ReadUntilAsync<TurnCompleted>();

        var (input, calledAt) = Assert.Single(calls);
        Assert.Equal(("PreToolUse", "Bash", "touch hooked.txt"), (input.EventName, input.ToolName, input.Command));
        Assert.Equal(requested.Request.ToolUseId, input.ToolUseId);
        Assert.True(askedAt - calledAt >= TimeSpan.FromSeconds(0.9), "The permission prompt should wait for the hook's answer.");
        Assert.True(File.Exists(Path.Combine(Work, "hooked.txt")));
    }

    [Fact]
    public async Task A_PreToolUse_hook_that_times_out_stops_the_command()
    {
        var hook = new HookRegistration("PreToolUse", "Bash", async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return HookOutputs.Continue();
        }, TimeSpan.FromSeconds(2));
        await using var session = await StartAsync(hooks: [hook]);

        await session.SendUserMessageAsync("RUN_BASH touch never.txt", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.DoesNotContain(seen, e => e is PermissionRequested);
        var result = seen.OfType<ToolResultsReceived>().Single().Message.Content.OfType<Core.Protocol.ToolResultBlock>().Single();
        Assert.True(result.IsError);
        Assert.Contains("did not respond before its timeout", result.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Work, "never.txt")));
    }

    [Fact]
    public async Task Subagent_traffic_and_prompts_name_the_subagent()
    {
        // Nesting is on by default, but a user's settings or environment can limit it; this test needs one level.
        await using var session = await StartAsync(environment: new() { ["CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH"] = "3" });

        await session.SendUserMessageAsync("SUBAGENTS", TestContext.Current.CancellationToken);
        var (requested, before) = await session.ReadUntilAsync<PermissionRequested>(timeout: TimeSpan.FromSeconds(30));
        requested.Request.Allow();
        var (done, after) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(30));
        var seen = before.Concat(after).ToArray();

        // DESIGN.md §18: task_started ties each subagent's task id to its Agent call, and a permission request from
        // inside a subagent names that task id in agent_id.
        var tasks = seen.OfType<SystemNotice>().Where(n => n.Message.Subtype == "task_started" && n.Message.Raw["task_type"]?.GetValue<string>() == "local_agent")
            .ToDictionary(n => n.Message.Raw["tool_use_id"]!.GetValue<string>(), n => n.Message.Raw["task_id"]!.GetValue<string>());
        var calls = seen.OfType<AssistantMessageReceived>()
            .SelectMany(a => a.Message.Content.OfType<Core.Protocol.ToolUseBlock>().Where(t => t.Name == "Agent").Select(t => (a.Message.ParentToolUseId, t)))
            .ToArray();
        Assert.Equal(3, calls.Length);
        var touch = calls.Single(c => c.t.Input["description"]?.GetValue<string>() == "Touch a marker file").t;
        var deeper = calls.Single(c => c.t.Input["description"]?.GetValue<string>() == "Delegate a deeper look").t;
        Assert.Equal(deeper.Id, calls.Single(c => c.t.Input["description"]?.GetValue<string>() == "Search deeper").ParentToolUseId);
        Assert.Equal(tasks[touch.Id], requested.Request.AgentId);
        Assert.Equal(3, tasks.Count);
        Assert.Contains(seen.OfType<AssistantMessageReceived>(), a => a.Message.ParentToolUseId == touch.Id);
        Assert.True(File.Exists(Path.Combine(Work, "agent-marker.txt")));
        Assert.False(done.Result.IsError);
    }

    [Fact]
    public async Task Stop_task_stops_one_foreground_subagent_and_the_turn_carries_on()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("LONG_AGENT", TestContext.Current.CancellationToken);
        var (started, _) = await session.ReadUntilAsync<SystemNotice>(
            n => n.Message.Subtype == "task_started" && n.Message.Raw["task_type"]?.GetValue<string>() == "local_agent", TimeSpan.FromSeconds(30));
        var taskId = started.Message.Raw["task_id"]!.GetValue<string>();
        var toolUseId = started.Message.Raw["tool_use_id"]!.GetValue<string>();
        Assert.False(started.Message.Raw["is_backgrounded"]?.GetValue<bool>());
        await session.ReadUntilAsync<AssistantMessageReceived>(a => a.Message.ParentToolUseId == toolUseId, TimeSpan.FromSeconds(30));

        await session.StopTaskAsync(taskId, TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(30));

        // DESIGN.md §18: the subagent is stopped, not the turn; its Agent call comes back as an error.
        Assert.Contains(seen.OfType<SystemNotice>(), n => n.Message.Subtype == "task_notification"
            && n.Message.Raw["task_id"]?.GetValue<string>() == taskId && n.Message.Raw["status"]?.GetValue<string>() == "stopped");
        var result = seen.OfType<ToolResultsReceived>().Where(r => r.Message.ParentToolUseId is null)
            .SelectMany(r => r.Message.Content.OfType<Core.Protocol.ToolResultBlock>()).Single(b => b.ToolUseId == toolUseId);
        Assert.True(result.IsError);
        Assert.False(done.Result.IsError);
        Assert.Equal("Done with the tool.", done.Result.Result);
    }

    [Fact]
    public async Task Remote_control_answers_with_its_eligibility_check()
    {
        await using var session = await StartAsync(environment: OutsideCloudSessions());

        var error = await Assert.ThrowsAsync<Core.Protocol.ControlRequestException>(() => session.EnableRemoteControlAsync("Test tab", TestContext.Current.CancellationToken));
        var (state, _) = await session.ReadUntilAsync<SystemNotice>(n => n.Message.Subtype == "bridge_state", TimeSpan.FromSeconds(10));

        // DESIGN.md §18: the real check ran. Against the mock, the custom ANTHROPIC_BASE_URL is what rules it out.
        Assert.Contains("Remote Control", error.Error, StringComparison.Ordinal);
        Assert.False(Core.RemoteControl.RemoteControlProtocol.IsUnsupported(error.Error));
        Assert.Equal(Core.RemoteControl.RemoteBridgeState.Failed, Core.RemoteControl.RemoteControlProtocol.BridgeState(state.Message.Raw));
        Assert.Equal(error.Error, state.Message.Raw["detail"]?.GetValue<string>());
        Assert.DoesNotContain(_api.Requests, r => r.Path.Contains("/messages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_remote_control_command_isnt_available_in_headless_mode()
    {
        await using var session = await StartAsync(environment: OutsideCloudSessions());

        await session.SendUserMessageAsync("/remote-control Test tab", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        // DESIGN.md §18: why Claudette connects with the remote_control request. A local command, whatever the account.
        var reply = seen.OfType<AssistantMessageReceived>().Single().Message;
        var text = string.Concat(reply.Content.OfType<Core.Protocol.TextBlock>().Select(b => b.Text));
        Assert.Equal("/remote-control isn't available in this environment.", text);
        Assert.Equal(Core.RemoteControl.RemoteControlProtocol.UnavailableHeadless, reply.Raw["local_command_outcome"]?["kind"]?.GetValue<string>());
        Assert.Equal(0, done.Result.Raw["num_turns"]?.GetValue<int>());
        Assert.Equal(Core.RemoteControl.RemoteControlState.Unavailable,
            Core.RemoteControl.RemoteControlProtocol.FromCommandReply(text, reply.Raw["local_command_outcome"]?["kind"]?.GetValue<string>()).State);
        Assert.DoesNotContain(_api.Requests, r => r.Path.Contains("/messages", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cloud session's own variables, which Claude Code doesn't set for its children, change what it says about
    /// Remote Control ("this session is already running as a cloud session"). Without them, as on a desktop.
    /// </summary>
    private static Dictionary<string, string?> OutsideCloudSessions() =>
        System.Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(name => name.StartsWith("CLAUDE_CODE_REMOTE", StringComparison.OrdinalIgnoreCase)
                || name is "CLAUDE_SESSION_INGRESS_TOKEN_FILE" or "CLAUDE_CODE_PROXY_RESOLVES_HOSTS" or "CLAUDE_CODE_CONTAINER_ID")
            .ToDictionary(name => name, _ => (string?)null);

    private async Task<ClaudeSession> StartAsync(string? permissionMode = null, string? resume = null, IReadOnlyList<HookRegistration>? hooks = null, string? appendSystemPrompt = null, Dictionary<string, string?>? environment = null,
        string model = "claude-haiku-4-5", bool replayUserMessages = false, bool includeHookEvents = false, bool forkSession = false, string? resumeSessionAt = null,
        string? fallbackModel = null, IReadOnlyList<string>? additionalArguments = null)
    {
        Assert.SkipWhen(_factory is null, "Claude Code isn't installed.");
        var overrides = new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = _api.BaseAddress.ToString().TrimEnd('/'),
            ["ANTHROPIC_API_KEY"] = "sk-ant-mock-000",
            ["ANTHROPIC_AUTH_TOKEN"] = null,
            ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
            ["CLAUDE_CONFIG_DIR"] = Config,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        };
        foreach (var (name, value) in environment ?? [])
        {
            overrides[name] = value;
        }
        return await _factory!.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = Work,
            Model = model,
            PermissionMode = permissionMode,
            Resume = resume,
            Hooks = hooks ?? [],
            AppendSystemPrompt = appendSystemPrompt,
            EnvironmentOverrides = overrides,
            ReplayUserMessages = replayUserMessages,
            IncludeHookEvents = includeHookEvents,
            ForkSession = forkSession,
            ResumeSessionAt = resumeSessionAt,
            FallbackModel = fallbackModel,
            AdditionalArguments = additionalArguments ?? [],
        }, TestContext.Current.CancellationToken);
    }
}
