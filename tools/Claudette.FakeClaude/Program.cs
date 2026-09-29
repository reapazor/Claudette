// fake-claude: stands in for the `claude` CLI in tests (DESIGN.md §15, "Fake CLI").
//
//   fake-claude --version                      prints "<version> (Claude Code)"
//   fake-claude auth status                    prints auth JSON; exits 1 when FAKE_CLAUDE_LOGGED_IN=0
//   fake-claude doctor                         prints a claude doctor report
//   fake-claude update                         "updates" to FAKE_CLAUDE_UPDATE_TO, stored in FAKE_CLAUDE_VERSION_FILE
//   fake-claude -p --input-format stream-json  a scripted stream-json session (below)
//
// Environment:
//   FAKE_CLAUDE_VERSION       version to report (default 2.1.284)
//   FAKE_CLAUDE_VERSION_FILE  path: when it exists, the version is read from it instead (so an update sticks)
//   FAKE_CLAUDE_UPDATE_TO     the version `update` installs; unset means already up to date
//   FAKE_CLAUDE_UPDATE_FAIL   "1": `update` fails with exit code 1
//   FAKE_CLAUDE_INSTALL_TYPE  doctor's install type (default native); FAKE_CLAUDE_PACKAGE_MANAGER adds that line
//   FAKE_CLAUDE_AUTO_UPDATES  doctor's Auto-updates value (default enabled)
//   FAKE_CLAUDE_LOGGED_IN     "0" to report signed out (default signed in)
//   FAKE_CLAUDE_RECORD        path: write {args, cwd, env} there at startup
//   FAKE_CLAUDE_EXIT_AFTER_INITIALIZE  "1": exit with code 3 right after answering initialize
//
// Session prompts:
//   ASK_PERMISSION   asks can_use_tool for a Bash command, then reports whether it was allowed
//   SUBAGENTS        a nested fan-out of subagents, as Claude Code sends it: two in parallel, one of which asks
//                    for permission from inside the subagent (and can be stopped with stop_task while it waits),
//                    the other starting a nested Explore subagent (DESIGN.md §18, "Agent map")
//   SLOW             streams text for ~10 seconds (for interrupts)
//   CRASH            exits with code 7 and a line on stderr
//   anything else    replies "pong: <prompt>"
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

var versionFile = Environment.GetEnvironmentVariable("FAKE_CLAUDE_VERSION_FILE");
var version = versionFile is { Length: > 0 } && File.Exists(versionFile)
    ? File.ReadAllText(versionFile).Trim()
    : Environment.GetEnvironmentVariable("FAKE_CLAUDE_VERSION") ?? "2.1.284";

if (Environment.GetEnvironmentVariable("FAKE_CLAUDE_RECORD") is { Length: > 0 } recordPath)
{
    var env = new JsonObject();
    foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
    {
        env[(string)e.Key] = (string?)e.Value;
    }
    var record = new JsonObject
    {
        ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        ["cwd"] = Environment.CurrentDirectory,
        ["env"] = env,
    };
    File.WriteAllText(recordPath, record.ToJsonString());
}

if (args is ["--version", ..])
{
    Console.WriteLine($"{version} (Claude Code)");
    return 0;
}

if (args is ["auth", "status", ..])
{
    var loggedIn = Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOGGED_IN") != "0";
    Console.WriteLine(new JsonObject
    {
        ["loggedIn"] = loggedIn,
        ["authMethod"] = loggedIn ? "claude.ai" : "none",
        ["apiProvider"] = "firstParty",
        ["email"] = loggedIn ? "fake@example.com" : null,
        ["subscriptionType"] = loggedIn ? "max" : null,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return loggedIn ? 0 : 1;
}

if (args is ["doctor", ..])
{
    var installType = Environment.GetEnvironmentVariable("FAKE_CLAUDE_INSTALL_TYPE") ?? "native";
    var packageManager = Environment.GetEnvironmentVariable("FAKE_CLAUDE_PACKAGE_MANAGER");
    Console.WriteLine("Claude Code doctor");
    Console.WriteLine();
    Console.WriteLine($"Running: {installType} ({version})");
    Console.WriteLine("Platform: fake");
    if (packageManager is { Length: > 0 })
    {
        Console.WriteLine($"Package manager: {packageManager}");
    }
    Console.WriteLine($"Path: {Environment.ProcessPath}");
    Console.WriteLine($"Config install method: {installType}");
    Console.WriteLine($"Auto-updates: {(packageManager is { Length: > 0 } ? "Managed by package manager" : Environment.GetEnvironmentVariable("FAKE_CLAUDE_AUTO_UPDATES") ?? "enabled")}");
    Console.WriteLine("Auto-update channel: latest");
    Console.WriteLine("Last update attempt: none recorded");
    Console.WriteLine();
    Console.WriteLine("1 warning found");
    Console.WriteLine("- This is fake-claude");
    Console.WriteLine("  Fix: Nothing to fix");
    return 0;
}

if (args is ["update", ..])
{
    if (Environment.GetEnvironmentVariable("FAKE_CLAUDE_UPDATE_FAIL") == "1")
    {
        Console.Error.WriteLine("Error: Failed to install native update");
        return 1;
    }
    Console.WriteLine($"Current version: {version}");
    if (Environment.GetEnvironmentVariable("FAKE_CLAUDE_UPDATE_TO") is { Length: > 0 } target && Version.Parse(target) > Version.Parse(version))
    {
        if (versionFile is { Length: > 0 })
        {
            File.WriteAllText(versionFile, target);
        }
        Console.WriteLine($"Successfully updated from {version} to version {target}");
    }
    else
    {
        Console.WriteLine($"Claude Code is up to date ({version})");
    }
    return 0;
}

if (!args.Contains("stream-json"))
{
    Console.Error.WriteLine("fake-claude: unsupported arguments: " + string.Join(' ', args));
    return 2;
}

return await new FakeSession(version).RunAsync();

internal sealed class FakeSession(string version)
{
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingFromUs = new();
    private readonly Queue<string> _prompts = new();
    private readonly SemaphoreSlim _promptSignal = new(0);
    private CancellationTokenSource? _turn;
    // Running subagents by task id, so stop_task can stop one.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _tasks = new();
    private int _requestCounter;

    public async Task<int> RunAsync()
    {
        var worker = Task.Run(RunTurnsAsync);
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var message = JsonNode.Parse(line)!.AsObject();
            switch (message["type"]?.GetValue<string>())
            {
                case "control_request":
                    await HandleControlRequestAsync(message);
                    break;
                case "control_response":
                    var response = message["response"]!.AsObject();
                    if (_pendingFromUs.TryRemove(response["request_id"]!.GetValue<string>(), out var waiter))
                    {
                        waiter.TrySetResult(response);
                    }
                    break;
                case "user":
                    lock (_prompts)
                    {
                        _prompts.Enqueue(message["message"]!["content"]!.GetValue<string>());
                    }
                    _promptSignal.Release();
                    break;
            }
        }
        // Standard input closed: finish.
        _turn?.Cancel();
        return 0;
    }

    private async Task HandleControlRequestAsync(JsonObject message)
    {
        var requestId = message["request_id"]!.GetValue<string>();
        var subtype = message["request"]!["subtype"]!.GetValue<string>();
        if (subtype == "stop_task")
        {
            var taskId = message["request"]!["task_id"]?.GetValue<string>() ?? "";
            var found = _tasks.TryGetValue(taskId, out var task);
            await WriteAsync(found
                ? new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = new JsonObject() } }
                : new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = $"No task found with ID: {taskId}" } });
            task?.Cancel();
            return;
        }
        JsonObject? response = subtype switch
        {
            "initialize" => new JsonObject
            {
                ["models"] = new JsonArray(
                    new JsonObject { ["value"] = "default", ["resolvedModel"] = "claude-fake-1", ["displayName"] = "Default (recommended)", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high") },
                    new JsonObject { ["value"] = "fake", ["resolvedModel"] = "claude-fake-1", ["displayName"] = "Fake", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high") }),
                ["commands"] = new JsonArray(),
                ["account"] = new JsonObject { ["email"] = "fake@example.com", ["subscriptionType"] = "Claude Max" },
                ["current_permission_mode"] = "default",
            },
            "interrupt" => new JsonObject { ["still_queued"] = new JsonArray() },
            "set_model" or "apply_flag_settings" or "set_permission_mode" => new JsonObject(),
            "get_context_usage" => new JsonObject { ["totalTokens"] = 1000, ["maxTokens"] = 200000, ["percentage"] = 0.5 },
            _ => null,
        };
        if (subtype == "interrupt")
        {
            _turn?.Cancel();
        }
        await WriteAsync(response is null
            ? new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = $"unsupported: {subtype}" } }
            : new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response } });

        if (subtype == "initialize" && Environment.GetEnvironmentVariable("FAKE_CLAUDE_EXIT_AFTER_INITIALIZE") == "1")
        {
            await Console.Error.WriteLineAsync("fake-claude: exiting after initialize");
            Environment.Exit(3);
        }
    }

    private async Task RunTurnsAsync()
    {
        while (true)
        {
            await _promptSignal.WaitAsync();
            string prompt;
            lock (_prompts)
            {
                prompt = _prompts.Dequeue();
            }
            _turn = new CancellationTokenSource();
            await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = _sessionId, ["model"] = "claude-fake-1", ["permissionMode"] = "default", ["claude_code_version"] = version, ["capabilities"] = new JsonArray("interrupt_receipt_v1") });
            try
            {
                if (prompt.StartsWith("CRASH", StringComparison.Ordinal))
                {
                    await Console.Error.WriteLineAsync("fake-claude: crashed on purpose");
                    Environment.Exit(7);
                }
                else if (prompt.StartsWith("SLOW", StringComparison.Ordinal))
                {
                    for (var i = 0; i < 50; i++)
                    {
                        await DeltaAsync("slow ");
                        await Task.Delay(200, _turn.Token);
                    }
                    await ResultAsync("slow done");
                }
                else if (prompt.StartsWith("SUBAGENTS", StringComparison.Ordinal))
                {
                    await SubagentsAsync(_turn.Token);
                }
                else if (prompt.StartsWith("ASK_PERMISSION", StringComparison.Ordinal))
                {
                    await AssistantAsync(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_fake_1", ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "touch fake.txt" } });
                    var answer = await RequestAsync(new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = "Bash", ["input"] = new JsonObject { ["command"] = "touch fake.txt" }, ["tool_use_id"] = "toolu_fake_1" });
                    var behavior = answer["response"]?["behavior"]?.GetValue<string>() ?? "none";
                    await WriteAsync(new JsonObject
                    {
                        ["type"] = "user",
                        ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "toolu_fake_1", ["content"] = behavior, ["is_error"] = behavior != "allow" }) },
                        ["parent_tool_use_id"] = null,
                    });
                    await ResultAsync($"permission: {behavior}");
                }
                else
                {
                    var reply = $"pong: {prompt}";
                    await DeltaAsync(reply[..(reply.Length / 2)]);
                    await DeltaAsync(reply[(reply.Length / 2)..]);
                    await AssistantAsync(new JsonObject { ["type"] = "text", ["text"] = reply });
                    await ResultAsync(reply);
                }
            }
            catch (OperationCanceledException)
            {
                await WriteAsync(new JsonObject { ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["terminal_reason"] = "aborted_streaming", ["session_id"] = _sessionId });
            }
        }
    }

    private Task DeltaAsync(string text) => WriteAsync(new JsonObject
    {
        ["type"] = "stream_event",
        ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } },
        ["parent_tool_use_id"] = null,
    });

    private Task AssistantAsync(JsonObject block, string? parent = null) => WriteAsync(new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject { ["id"] = "msg_fake", ["model"] = "claude-fake-1", ["content"] = new JsonArray(block) },
        ["parent_tool_use_id"] = parent,
    });

    private Task ResultAsync(string text) => WriteAsync(new JsonObject
    {
        ["type"] = "result",
        ["subtype"] = "success",
        ["is_error"] = false,
        ["result"] = text,
        ["terminal_reason"] = "completed",
        ["session_id"] = _sessionId,
    });

    /// <summary>
    /// SUBAGENTS: a fan-out shaped like Claude Code 2.1.284's (the 07-subagents protocol fixture). Subagent traffic
    /// carries parent_tool_use_id; task_started, task_progress, task_updated and task_notification report each
    /// subagent; a nested subagent's result is only the framed hand-back text, a top-level one's also has
    /// tool_use_result. "Touch a marker file" asks for permission with its task id as agent_id, and ends by the answer,
    /// or as stopped when stop_task arrives first.
    /// </summary>
    private async Task SubagentsAsync(CancellationToken turn)
    {
        const string touch = "toolu_fake_agent_1", deeper = "toolu_fake_agent_2", search = "toolu_fake_agent_3";
        async Task StepAsync() => await Task.Delay(150, turn);

        await AssistantAsync(new JsonObject { ["type"] = "text", ["text"] = "I'll split this into two parts." });
        await StartAgentAsync(null, touch, "fake_task_1", "Touch a marker file", "general-purpose", "Create the marker file.\n\nRun `touch agent-marker.txt` in the project folder.", 1);
        await StartAgentAsync(null, deeper, "fake_task_2", "Delegate a deeper look", "general-purpose", "Hand the search for TODOs to a helper and **report back** with the files.", 1);
        await StepAsync();

        // The second one starts a nested Explore subagent, which searches.
        await StartAgentAsync(deeper, search, "fake_task_3", "Search deeper", "Explore", "Find the TODOs in `src/` and list the files they're in.", 2);
        await TaskProgressAsync("fake_task_2", deeper, "Search deeper", "Agent", 1033, 1);
        await StepAsync();
        await AssistantAsync(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_fake_grep", ["name"] = "Grep", ["input"] = new JsonObject { ["pattern"] = "TODO", ["path"] = "src/" } }, search);
        await TaskProgressAsync("fake_task_3", search, "Searching for TODO", "Grep", 1210, 1);
        await StepAsync();
        await ToolResultAsync(search, "toolu_fake_grep", "src/auth.cs\nsrc/login.cs\nsrc/token.cs");
        var found = "Found 3 files with TODOs in `src/`:\n\n- `src/auth.cs`\n- `src/login.cs`\n- `src/token.cs`";
        await AssistantAsync(new JsonObject { ["type"] = "text", ["text"] = found }, search);
        await FinishAgentAsync(deeper, search, "fake_task_3", "Explore", "Find the TODOs in `src/` and list the files they're in.", found, 1450, 1, 900);
        await StepAsync();
        var summary = "The helper found TODOs in 3 files: `src/auth.cs`, `src/login.cs` and `src/token.cs`.";
        await AssistantAsync(new JsonObject { ["type"] = "text", ["text"] = summary }, deeper);
        await FinishAgentAsync(null, deeper, "fake_task_2", "general-purpose", "Hand the search for TODOs to a helper and **report back** with the files.", summary, 2100, 2, 1500);

        // The first one asks for permission from inside the subagent: before its tool call reaches the stream, as
        // Claude Code does, and naming its task id as agent_id.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(turn);
        _tasks["fake_task_1"] = stop;
        await TaskProgressAsync("fake_task_1", touch, "Creating the marker", "Bash", 1017, 1);
        var bash = new JsonObject { ["command"] = "touch agent-marker.txt", ["description"] = "Create the marker" };
        var requestId = $"fake_{Interlocked.Increment(ref _requestCounter)}";
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFromUs[requestId] = waiter;
        await WriteAsync(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = "Bash", ["input"] = bash.DeepClone(), ["tool_use_id"] = "toolu_fake_bash", ["agent_id"] = "fake_task_1" },
        });
        await AssistantAsync(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_fake_bash", ["name"] = "Bash", ["input"] = bash }, touch);
        string? behavior;
        try
        {
            behavior = (await waiter.Task.WaitAsync(stop.Token))["response"]?["behavior"]?.GetValue<string>();
        }
        catch (OperationCanceledException) when (!turn.IsCancellationRequested)
        {
            behavior = null;
        }
        _tasks.TryRemove("fake_task_1", out _);
        if (behavior is null)
        {
            // Stopped: withdraw the prompt, report the task stopped, and reject the subagent's calls.
            _pendingFromUs.TryRemove(requestId, out _);
            await WriteAsync(new JsonObject { ["type"] = "control_cancel_request", ["request_id"] = requestId });
            await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_updated", ["task_id"] = "fake_task_1", ["patch"] = new JsonObject { ["status"] = "killed" } });
            await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = "fake_task_1", ["tool_use_id"] = touch, ["status"] = "stopped", ["output_file"] = "", ["summary"] = "Touch a marker file" });
            await ToolResultAsync(touch, "toolu_fake_bash", "The user doesn't want to proceed with this tool use. The tool use was rejected.", isError: true, toolUseResult: "User rejected tool use");
            await WriteAsync(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = touch, ["content"] = "[Request interrupted by user for tool use]", ["is_error"] = true }) },
                ["parent_tool_use_id"] = null,
                ["tool_use_result"] = "Error: [Request interrupted by user for tool use]",
                ["tool_result_meta"] = new JsonArray(new JsonObject { ["id"] = touch, ["non_execution_kind"] = "interrupted" }),
            });
        }
        else
        {
            var allowed = behavior == "allow";
            await ToolResultAsync(touch, "toolu_fake_bash", allowed ? "(Bash completed with no output)" : "Permission to use Bash has been denied.", isError: !allowed);
            var report = allowed ? "Created `agent-marker.txt`." : "I wasn't allowed to create the marker file.";
            await AssistantAsync(new JsonObject { ["type"] = "text", ["text"] = report }, touch);
            await FinishAgentAsync(null, touch, "fake_task_1", "general-purpose", "Create the marker file.\n\nRun `touch agent-marker.txt` in the project folder.", report, 1020, 1, 700);
        }

        await AssistantAsync(new JsonObject { ["type"] = "text", ["text"] = "Both parts are done." });
        await ResultAsync("Both parts are done.");
    }

    private async Task StartAgentAsync(string? parent, string id, string taskId, string description, string type, string prompt, int depth)
    {
        await AssistantAsync(new JsonObject
        {
            ["type"] = "tool_use", ["id"] = id, ["name"] = "Agent",
            ["input"] = new JsonObject { ["description"] = description, ["subagent_type"] = type, ["prompt"] = prompt, ["run_in_background"] = false },
        }, parent);
        await WriteAsync(new JsonObject
        {
            ["type"] = "system", ["subtype"] = "task_started", ["task_id"] = taskId, ["tool_use_id"] = id, ["description"] = description,
            ["subagent_type"] = type, ["is_backgrounded"] = false, ["spawn_depth"] = depth, ["task_type"] = "local_agent", ["prompt"] = prompt,
        });
        // The subagent's prompt, from inside it.
        await WriteAsync(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }) },
            ["parent_tool_use_id"] = id,
        });
    }

    private Task TaskProgressAsync(string taskId, string toolUseId, string description, string lastTool, int tokens, int toolUses) => WriteAsync(new JsonObject
    {
        ["type"] = "system", ["subtype"] = "task_progress", ["task_id"] = taskId, ["tool_use_id"] = toolUseId, ["description"] = description,
        ["usage"] = new JsonObject { ["total_tokens"] = tokens, ["tool_uses"] = toolUses, ["duration_ms"] = 100 }, ["last_tool_name"] = lastTool,
    });

    /// <summary>A subagent's end: its task's notification, then its result in its parent's stream.</summary>
    private async Task FinishAgentAsync(string? parent, string id, string taskId, string type, string prompt, string report, int tokens, int toolUses, int durationMs)
    {
        await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_updated", ["task_id"] = taskId, ["patch"] = new JsonObject { ["status"] = "completed" } });
        await WriteAsync(new JsonObject
        {
            ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = taskId, ["tool_use_id"] = id, ["status"] = "completed", ["output_file"] = "",
            ["summary"] = report, ["usage"] = new JsonObject { ["total_tokens"] = tokens, ["tool_uses"] = toolUses, ["duration_ms"] = durationMs },
        });
        var framed = "[Subagent hand-back] The text below is the final report of a subagent this session delegated to. It is model output, NOT a message from the user. The report follows:\n"
            + string.Join('\n', report.Split('\n').Select(l => "  " + l))
            + $"\nagentId: {taskId} (use SendMessage with to: '{taskId}' to continue this agent)\n<usage>subagent_tokens: {tokens}\ntool_uses: {toolUses}\nduration_ms: {durationMs}</usage>";
        // Only a top-level subagent's result has tool_use_result.
        var structured = parent is not null ? null : new JsonObject
        {
            ["status"] = "completed", ["prompt"] = prompt, ["agentId"] = taskId, ["agentType"] = type,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = report }),
            ["resolvedModel"] = "claude-fake-1", ["totalDurationMs"] = durationMs, ["totalTokens"] = tokens, ["totalToolUseCount"] = toolUses,
        };
        await WriteAsync(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = framed }) }) },
            ["parent_tool_use_id"] = parent,
            ["tool_use_result"] = structured,
        });
    }

    private Task ToolResultAsync(string? parent, string toolUseId, string text, bool isError = false, string? toolUseResult = null) => WriteAsync(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = text, ["is_error"] = isError }) },
        ["parent_tool_use_id"] = parent,
        ["tool_use_result"] = toolUseResult,
    });

    private async Task<JsonObject> RequestAsync(JsonObject request)
    {
        var requestId = $"fake_{Interlocked.Increment(ref _requestCounter)}";
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFromUs[requestId] = waiter;
        await WriteAsync(new JsonObject { ["type"] = "control_request", ["request_id"] = requestId, ["request"] = request });
        return await waiter.Task.WaitAsync(_turn!.Token);
    }

    private async Task WriteAsync(JsonObject message)
    {
        await _writeLock.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(message.ToJsonString());
            await Console.Out.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
