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
//   FAKE_CLAUDE_LOGGED_IN     "0" to start signed out (default signed in); see "Sign-in" below
//   FAKE_CLAUDE_RECORD        path: write {args, cwd, env} there at startup
//   FAKE_CLAUDE_EXIT_AFTER_INITIALIZE  "1": exit with code 3 right after answering initialize
//   FAKE_CLAUDE_USAGE         answers get_usage (DESIGN.md §6): a path to a recorded response, or "demo" for a plan
//                             whose session use starts at 35% and climbs 0.6% a minute, so the header's meters,
//                             sparkline and projection can be seen without an account. Unset: get_usage fails.
//
// Session prompts:
//   ASK_PERMISSION   asks can_use_tool for a Bash command, then reports whether it was allowed
//   RUN_BASH <cmd>   a Bash tool call: calls back any PreToolUse hooks registered with initialize for Bash
//                    (hook_callback, as Claude Code does), then "runs" it; the result is FAKE_CLAUDE_BASH_OUTPUT, or
//                    "ran: <cmd>". A hook that doesn't answer within its timeout is cancelled and the command isn't run.
//   SUBAGENTS        a nested fan-out of subagents, as Claude Code sends it: two in parallel, one of which asks
//                    for permission from inside the subagent (and can be stopped with stop_task while it waits),
//                    the other starting a nested Explore subagent (DESIGN.md §18, "Agent map")
//   SLOW             streams text for ~10 seconds (for interrupts)
//   CRASH            exits with code 7 and a line on stderr
//   SPAWN [s] [busy] starts a child process that runs for s seconds (default 30), using CPU when "busy", and ends the
//                    turn while it keeps running, like a dev server (for the process monitor)
//   SILENT           streams a little, then goes quiet until a message arrives mid-turn (a check-in), answers it with a
//                    status and ends the turn
//   HANG             goes quiet and ignores everything until interrupted (a stuck turn)
//   AUTH_FAIL        answers like a signed-out Claude Code: an assistant message with error "authentication_failed"
//   anything else    replies "pong: <prompt>" (or, while signed out, as AUTH_FAIL does)
// A message with images (content blocks) is read as its text plus "[images: image/png, …]", so the reply says what
// arrived.
//
//   fake-claude --child <seconds> [busy]   the child process SPAWN starts
//
// Sign-in (DESIGN.md §11), with a fake browser step:
//   fake-claude auth login [--console|--sso]   prints a sign-in URL like the real one, then finishes as FAKE_CLAUDE_LOGIN says
//   fake-claude auth logout                    signs out
//   claude_authenticate, claude_oauth_wait_for_completion and claude_oauth_callback in a session do the same
//   Signed in or out is kept in fake-claude-auth.json in CLAUDE_CONFIG_DIR, so a sign-in sticks for later commands and
//   sessions; without CLAUDE_CONFIG_DIR, or before the first sign-in or out, FAKE_CLAUDE_LOGGED_IN decides.
//   FAKE_CLAUDE_LOGIN         how the browser step ends: "auto" (default) finishes on its own after
//                             FAKE_CLAUDE_LOGIN_DELAY_MS (default 500); "code" waits for the code the fake page shows,
//                             FAKE-CODE#fake-state, on stdin or through claude_oauth_callback; "fail" fails with
//                             FAKE_CLAUDE_LOGIN_ERROR; "hang" never finishes
//   FAKE_CLAUDE_AUTHENTICATE  "unsupported": claude_authenticate is rejected, as by a Claude Code without it
//   FAKE_CLAUDE_LOGOUT_FAIL   "1": auth logout fails
//   FAKE_CLAUDE_START_AUTH_ERROR  "1": a session fails to start with an authentication error on stderr
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

if (args is ["--child", var childSeconds, .. var childOptions])
{
    // SPAWN's child: sleeps, or keeps a core busy, until its time is up.
    var until = DateTime.UtcNow.AddSeconds(double.Parse(childSeconds, System.Globalization.CultureInfo.InvariantCulture));
    while (DateTime.UtcNow < until)
    {
        if (childOptions.Contains("busy"))
        {
            Thread.SpinWait(1_000_000);
        }
        else
        {
            Thread.Sleep(100);
        }
    }
    return 0;
}

if (args is ["auth", "status", ..])
{
    var loggedIn = FakeAuth.IsLoggedIn();
    var status = new JsonObject
    {
        ["loggedIn"] = loggedIn,
        ["authMethod"] = loggedIn ? "claude.ai" : "none",
        ["apiProvider"] = "firstParty",
        ["email"] = loggedIn ? "fake@example.com" : null,
        ["subscriptionType"] = loggedIn ? FakeAuth.SubscriptionType() : null,
    };
    if (FakeAuth.ConfigDirectory is { } configDirectory)
    {
        status["projectsDirectory"] = Path.Combine(configDirectory, "projects");
        status["configDirectory"] = configDirectory;
    }
    Console.WriteLine(status.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return loggedIn ? 0 : 1;
}

if (args is ["auth", "login", ..])
{
    return await FakeAuth.LoginAsync(args[2..]);
}

if (args is ["auth", "logout", ..])
{
    if (Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOGOUT_FAIL") == "1")
    {
        Console.Error.WriteLine("Logout failed: fake-claude couldn't remove the credentials");
        return 1;
    }
    FakeAuth.SetLoggedIn(false, null);
    Console.WriteLine("Successfully logged out from your Anthropic account.");
    return 0;
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

if (Environment.GetEnvironmentVariable("FAKE_CLAUDE_START_AUTH_ERROR") == "1")
{
    Console.Error.WriteLine("Invalid API key · Please run /login");
    return 1;
}

var modeFlag = Array.IndexOf(args, "--permission-mode");
return await new FakeSession(version, modeFlag >= 0 && modeFlag + 1 < args.Length ? args[modeFlag + 1] : "default").RunAsync();

internal sealed class FakeSession(string version, string permissionMode)
{
    private readonly string _sessionId = Guid.NewGuid().ToString();
    // Reported as Claude Code reports it: the --permission-mode it started with, then set_permission_mode's.
    private string _permissionMode = permissionMode;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingFromUs = new();
    private readonly Queue<string> _prompts = new();
    private readonly SemaphoreSlim _promptSignal = new(0);
    private CancellationTokenSource? _turn;
    // Running subagents by task id, so stop_task can stop one.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _tasks = new();
    private int _requestCounter;
    private readonly List<(string? Matcher, List<string> CallbackIds, int? Timeout)> _preToolUseHooks = [];

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
                        _prompts.Enqueue(PromptText(message["message"]?["content"]));
                    }
                    _promptSignal.Release();
                    break;
            }
        }
        // Standard input closed: finish.
        _turn?.Cancel();
        return 0;
    }

    /// <summary>A plain-text prompt, or content blocks: their text, then the attached images' media types.</summary>
    private static string PromptText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }
        var blocks = (content as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var texts = blocks.Where(b => b["type"]?.GetValue<string>() == "text").Select(b => b["text"]?.GetValue<string>() ?? "");
        var images = blocks.Where(b => b["type"]?.GetValue<string>() == "image").Select(b => b["source"]?["media_type"]?.GetValue<string>() ?? "unknown").ToArray();
        var prompt = string.Join("\n", texts);
        return images.Length == 0 ? prompt : $"{prompt} [images: {string.Join(", ", images)}]".TrimStart();
    }

    private async Task HandleControlRequestAsync(JsonObject message)
    {
        var requestId = message["request_id"]!.GetValue<string>();
        var subtype = message["request"]!["subtype"]!.GetValue<string>();
        if (subtype is "claude_authenticate" or "claude_oauth_wait_for_completion" or "claude_oauth_callback")
        {
            await HandleSignInAsync(requestId, subtype, message["request"]!.AsObject());
            return;
        }
        if (subtype == "initialize" && message["request"]!["hooks"]?["PreToolUse"] is JsonArray matchers)
        {
            foreach (var matcher in matchers.OfType<JsonObject>())
            {
                _preToolUseHooks.Add((
                    matcher["matcher"]?.GetValue<string>(),
                    matcher["hookCallbackIds"]?.AsArray().Select(id => id!.GetValue<string>()).ToList() ?? [],
                    matcher["timeout"]?.GetValue<int>()));
            }
        }
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
                    new JsonObject { ["value"] = "default", ["resolvedModel"] = "claude-fake-1", ["displayName"] = "Default (recommended)", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high"), ["supportsAutoMode"] = true },
                    new JsonObject { ["value"] = "fake", ["resolvedModel"] = "claude-fake-1", ["displayName"] = "Fake", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high"), ["supportsAutoMode"] = true }),
                ["commands"] = new JsonArray(
                    new JsonObject { ["name"] = "review", ["description"] = "Review the changes (project)", ["argumentHint"] = "[path]" },
                    new JsonObject { ["name"] = "compact", ["description"] = "Free up context by summarizing the conversation so far", ["argumentHint"] = "<optional custom summarization instructions>", ["builtin"] = true }),
                ["account"] = new JsonObject { ["email"] = "fake@example.com", ["subscriptionType"] = "Claude Max" },
                ["current_permission_mode"] = _permissionMode,
            },
            "interrupt" => new JsonObject { ["still_queued"] = new JsonArray() },
            "set_model" or "apply_flag_settings" or "set_permission_mode" => new JsonObject(),
            "get_context_usage" => new JsonObject { ["totalTokens"] = 1000, ["maxTokens"] = 200000, ["percentage"] = 0.5 },
            "get_usage" => Usage(),
            _ => null,
        };
        if (subtype == "interrupt")
        {
            _turn?.Cancel();
        }
        if (subtype == "set_permission_mode" && message["request"]!["mode"]?.GetValue<string>() is { } mode)
        {
            _permissionMode = mode;
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

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    /// <summary>The get_usage response FAKE_CLAUDE_USAGE asks for, or null to fail the request.</summary>
    private static JsonObject? Usage()
    {
        var setting = Environment.GetEnvironmentVariable("FAKE_CLAUDE_USAGE");
        if (setting is not { Length: > 0 })
        {
            return null;
        }
        if (setting != "demo")
        {
            return JsonNode.Parse(File.ReadAllText(setting))?.AsObject();
        }
        var session = Math.Min(100, 35 + 0.6 * (DateTimeOffset.UtcNow - Started).TotalMinutes);
        static JsonObject Limit(string kind, double percent, DateTimeOffset resets, string? model = null) => new()
        {
            ["kind"] = kind,
            ["group"] = kind == "session" ? "session" : "weekly",
            ["percent"] = Math.Round(percent),
            ["severity"] = percent >= 90 ? "critical" : percent >= 75 ? "warning" : "normal",
            ["resets_at"] = resets.ToString("o"),
            ["scope"] = model is null ? null : new JsonObject { ["model"] = new JsonObject { ["id"] = null, ["display_name"] = model }, ["surface"] = null },
            ["is_active"] = percent >= 100,
        };
        return new JsonObject
        {
            ["subscription_type"] = "max",
            ["rate_limits_available"] = true,
            ["rate_limits"] = new JsonObject
            {
                ["limits"] = new JsonArray(
                    Limit("session", session, Started.AddHours(2)),
                    Limit("weekly_all", 57, Started.AddDays(3)),
                    Limit("weekly_scoped", 20, Started.AddDays(3), "Fable")),
            },
        };
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
            await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = _sessionId, ["model"] = "claude-fake-1", ["permissionMode"] = _permissionMode, ["claude_code_version"] = version, ["capabilities"] = new JsonArray("interrupt_receipt_v1"), ["slash_commands"] = new JsonArray("review", "compact") });
            try
            {
                if (prompt.StartsWith("AUTH_FAIL", StringComparison.Ordinal) || !FakeAuth.IsLoggedIn())
                {
                    // As Claude Code 2.1.284 answers when it isn't signed in (the signed-out protocol fixture).
                    const string text = "Not logged in · Please run /login";
                    await WriteAsync(new JsonObject
                    {
                        ["type"] = "assistant",
                        ["message"] = new JsonObject { ["id"] = "msg_fake", ["model"] = "<synthetic>", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) },
                        ["parent_tool_use_id"] = null,
                        ["error"] = "authentication_failed",
                        ["is_api_error_message"] = true,
                    });
                    await WriteAsync(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["result"] = text, ["terminal_reason"] = "api_error", ["session_id"] = _sessionId });
                }
                else if (prompt.StartsWith("CRASH", StringComparison.Ordinal))
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
                else if (prompt.StartsWith("SPAWN", StringComparison.Ordinal))
                {
                    var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var seconds = words.Length > 1 && int.TryParse(words[1], out var s) ? s : 30;
                    var child = StartChild(seconds, words.Contains("busy"));
                    await ResultAsync($"started child {child.Id}");
                }
                else if (prompt.StartsWith("SILENT", StringComparison.Ordinal))
                {
                    await DeltaAsync("working on it… ");
                    await _promptSignal.WaitAsync(_turn.Token);
                    lock (_prompts)
                    {
                        _prompts.Dequeue();
                    }
                    await AssistantAsync(new JsonObject { ["type"] = "text", ["text"] = "Status: waiting on a long build; not stuck." });
                    await ResultAsync("status sent");
                }
                else if (prompt.StartsWith("HANG", StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.Infinite, _turn.Token);
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
                else if (prompt.StartsWith("RUN_BASH ", StringComparison.Ordinal))
                {
                    await RunBashAsync(prompt["RUN_BASH ".Length..].Trim());
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

    /// <summary>Starts this program again as a child process that outlives the turn.</summary>
    private static System.Diagnostics.Process StartChild(int seconds, bool busy)
    {
        var exe = Environment.ProcessPath!;
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
        {
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        }
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (busy)
        {
            start.ArgumentList.Add("busy");
        }
        return System.Diagnostics.Process.Start(start)!;
    }

    // ---- Sign-in through the control protocol (DESIGN.md §11) ----------------------------------------------------

    private FakeSignIn? _signIn;

    /// <summary>
    /// claude_authenticate starts a fake sign-in and returns its two URLs; claude_oauth_wait_for_completion and
    /// claude_oauth_callback answer once it has finished, with the account or the sign-in's error, as Claude Code does.
    /// </summary>
    private async Task HandleSignInAsync(string requestId, string subtype, JsonObject request)
    {
        if (subtype == "claude_authenticate")
        {
            if (Environment.GetEnvironmentVariable("FAKE_CLAUDE_AUTHENTICATE") == "unsupported")
            {
                await RespondAsync(requestId, null, $"Unsupported control request subtype: {subtype}");
                return;
            }
            var method = request["loginWithClaudeAi"]?.GetValue<bool>() == false ? "console" : "claude.ai";
            var signIn = _signIn = new FakeSignIn(method);
            signIn.Start();
            await RespondAsync(requestId, new JsonObject
            {
                ["manualUrl"] = FakeAuth.Url(method, manual: true),
                ["automaticUrl"] = FakeAuth.Url(method, manual: false),
            }, null);
            return;
        }
        if (_signIn is not { } flow)
        {
            await RespondAsync(requestId, null, "No active claude_authenticate flow");
            return;
        }
        if (subtype == "claude_oauth_callback" && !flow.EnterCode($"{request["authorizationCode"]?.GetValue<string>()}#{request["state"]?.GetValue<string>()}"))
        {
            flow.Fail("Invalid authorization code");
        }
        // Answered once the sign-in finishes, without holding up the other requests meanwhile.
        _ = Task.Run(async () =>
        {
            var error = await flow.Done;
            var account = new JsonObject { ["account"] = new JsonObject { ["email"] = "fake@example.com", ["subscriptionType"] = FakeAuth.SubscriptionType() } };
            await RespondAsync(requestId, error is null ? account : null, error);
        });
    }

    private Task RespondAsync(string requestId, JsonObject? response, string? error) => WriteAsync(error is null
        ? new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response ?? [] } }
        : new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = error } });

    /// <summary>A Bash tool call, with the PreToolUse hooks Claude Code would call back first.</summary>
    private async Task RunBashAsync(string command)
    {
        var toolUseId = $"toolu_fake_{Interlocked.Increment(ref _requestCounter)}";
        var input = new JsonObject { ["command"] = command, ["description"] = "fake command" };
        await AssistantAsync(new JsonObject { ["type"] = "tool_use", ["id"] = toolUseId, ["name"] = "Bash", ["input"] = input.DeepClone() });
        var blocked = false;
        foreach (var (matcher, ids, timeout) in _preToolUseHooks.Where(h => h.Matcher is null or "" or "*" || h.Matcher.Split('|').Contains("Bash")))
        {
            foreach (var id in ids)
            {
                var request = new JsonObject
                {
                    ["subtype"] = "hook_callback",
                    ["callback_id"] = id,
                    ["input"] = new JsonObject
                    {
                        ["session_id"] = _sessionId,
                        ["cwd"] = Environment.CurrentDirectory,
                        ["permission_mode"] = _permissionMode,
                        ["hook_event_name"] = "PreToolUse",
                        ["tool_name"] = "Bash",
                        ["tool_input"] = input.DeepClone(),
                        ["tool_use_id"] = toolUseId,
                    },
                    ["tool_use_id"] = toolUseId,
                };
                if (await RequestAsync(request, TimeSpan.FromSeconds(timeout ?? 60)) is null)
                {
                    blocked = true;
                }
            }
        }
        var output = blocked
            ? "PreToolUse hook did not respond before its timeout (host client may be unreachable). The tool call was not executed; other configured hooks may not have completed."
            : Environment.GetEnvironmentVariable("FAKE_CLAUDE_BASH_OUTPUT") ?? $"ran: {command}";
        await WriteAsync(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = output, ["is_error"] = blocked }) },
            ["parent_tool_use_id"] = null,
            ["tool_use_result"] = blocked ? $"Error: {output}" : new JsonObject { ["stdout"] = output, ["stderr"] = "", ["interrupted"] = false },
        });
        await ResultAsync(blocked ? "The hook timed out." : "Done with the tool.");
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
    /// SUBAGENTS: a fan-out shaped like Claude Code 2.1.284's (the 12-subagents protocol fixture). Subagent traffic
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

    /// <summary>Like Claude Code: a request not answered in time is withdrawn with control_cancel_request. Null then.</summary>
    private async Task<JsonObject?> RequestAsync(JsonObject request, TimeSpan timeout)
    {
        var requestId = $"fake_{Interlocked.Increment(ref _requestCounter)}";
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFromUs[requestId] = waiter;
        await WriteAsync(new JsonObject { ["type"] = "control_request", ["request_id"] = requestId, ["request"] = request });
        try
        {
            return await waiter.Task.WaitAsync(timeout, _turn!.Token);
        }
        catch (TimeoutException)
        {
            _pendingFromUs.TryRemove(requestId, out _);
            await WriteAsync(new JsonObject { ["type"] = "control_cancel_request", ["request_id"] = requestId });
            return null;
        }
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

/// <summary>fake-claude's sign-in state: a file in CLAUDE_CONFIG_DIR, so a sign-in sticks for later commands.</summary>
internal static class FakeAuth
{
    public static string? ConfigDirectory => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } directory ? directory : null;

    private static string? StateFile => ConfigDirectory is { } directory ? Path.Combine(directory, "fake-claude-auth.json") : null;

    public static bool IsLoggedIn() => ReadState()?["loggedIn"]?.GetValue<bool>()
        ?? Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOGGED_IN") != "0";

    /// <summary>A Console account has no subscription.</summary>
    public static string? SubscriptionType() => ReadState()?["method"]?.GetValue<string>() == "console" ? null : "max";

    public static void SetLoggedIn(bool loggedIn, string? method)
    {
        if (StateFile is { } file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, new JsonObject { ["loggedIn"] = loggedIn, ["method"] = method }.ToJsonString());
        }
    }

    /// <summary>The fake sign-in page: the manual one shows the code FAKE-CODE#fake-state. Never resolves.</summary>
    public static string Url(string method, bool manual) => manual
        ? $"https://fake-claude.invalid/oauth/authorize?code=true&method={method}&state=fake-state"
        : $"https://fake-claude.invalid/oauth/authorize?method={method}&state=fake-state&redirect_uri=http%3A%2F%2Flocalhost%3A54545%2Fcallback";

    /// <summary><c>auth login</c>: prints what Claude Code 2.1.284 prints with no terminal, then reads codes from stdin.</summary>
    public static async Task<int> LoginAsync(string[] options)
    {
        if (options.Contains("--console") && options.Contains("--claudeai"))
        {
            Console.Error.WriteLine("Error: --console and --claudeai cannot be used together.");
            return 1;
        }
        var method = options.Contains("--console") ? "console" : options.Contains("--sso") ? "sso" : "claude.ai";
        var signIn = new FakeSignIn(method);
        Console.WriteLine("Opening browser to sign in…");
        Console.WriteLine($"If the browser didn't open, visit: {Url(method, manual: true)}");
        Console.Write("Paste code here if prompted > ");
        Console.Out.Flush();
        signIn.Start();
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (!signIn.EnterCode(line))
                {
                    Console.Error.WriteLine("Invalid code. Please make sure the full code was copied.");
                }
            }
        });
        var error = await signIn.Done;
        if (error is not null)
        {
            Console.Error.WriteLine($"Login failed: {error}");
            return 1;
        }
        Console.WriteLine("Login successful.");
        return 0;
    }

    private static JsonNode? ReadState()
    {
        if (StateFile is not { } file || !File.Exists(file))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>One fake sign-in: the browser step ends as FAKE_CLAUDE_LOGIN says, or with the code from the fake page.</summary>
internal sealed class FakeSignIn(string method)
{
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Null once signed in, or the sign-in's error.</summary>
    public Task<string?> Done => _done.Task;

    public void Start()
    {
        var delay = TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOGIN_DELAY_MS"), out var ms) ? ms : 500);
        switch (Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOGIN") ?? "auto")
        {
            case "auto":
                _ = Task.Delay(delay).ContinueWith(_ => Succeed(), TaskScheduler.Default);
                break;
            case "fail":
                var error = Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOGIN_ERROR") ?? "OAuth error: access_denied (the sign-in was cancelled in the browser)";
                _ = Task.Delay(delay).ContinueWith(_ => Fail(error), TaskScheduler.Default);
                break;
            default:
                // "code" waits for EnterCode; "hang" never finishes.
                break;
        }
    }

    /// <summary>A <c>code#state</c> from the fake page. False when it isn't in that form.</summary>
    public bool EnterCode(string code)
    {
        var parts = code.Trim().Split('#');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }
        if (parts[0] == "FAKE-CODE" && parts[1] == "fake-state")
        {
            Succeed();
        }
        else
        {
            Fail("Invalid authorization code");
        }
        return true;
    }

    public void Fail(string error) => _done.TrySetResult(error);

    private void Succeed()
    {
        if (!_done.Task.IsCompleted)
        {
            FakeAuth.SetLoggedIn(true, method);
            _done.TrySetResult(null);
        }
    }
}
