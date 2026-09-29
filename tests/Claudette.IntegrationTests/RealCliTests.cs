using System.Diagnostics;
using System.Text.Json.Nodes;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
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

    /// <summary>An RGB PNG with a gradient, which compresses well, so a big one stays small.</summary>
    private static byte[] Gradient(int width, int height)
    {
        var rows = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
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

    private async Task<ClaudeSession> StartAsync(string? permissionMode = null, string? resume = null)
    {
        Assert.SkipWhen(_factory is null, "Claude Code isn't installed.");
        return await _factory!.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = Work,
            Model = "claude-haiku-4-5",
            PermissionMode = permissionMode,
            Resume = resume,
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["ANTHROPIC_BASE_URL"] = _api.BaseAddress.ToString().TrimEnd('/'),
                ["ANTHROPIC_API_KEY"] = "sk-ant-mock-000",
                ["ANTHROPIC_AUTH_TOKEN"] = null,
                ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
                ["CLAUDE_CONFIG_DIR"] = Config,
                ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            },
        }, TestContext.Current.CancellationToken);
    }
}
