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
//   SLOW             streams text for ~10 seconds (for interrupts)
//   CRASH            exits with code 7 and a line on stderr
//   SPAWN [s] [busy] starts a child process that runs for s seconds (default 30), using CPU when "busy", and ends the
//                    turn while it keeps running, like a dev server (for the process monitor)
//   SILENT           streams a little, then goes quiet until a message arrives mid-turn (a check-in), answers it with a
//                    status and ends the turn
//   HANG             goes quiet and ignores everything until interrupted (a stuck turn)
//   AUTH_FAIL        answers like a signed-out Claude Code: an assistant message with error "authentication_failed"
//   anything else    replies "pong: <prompt>" (or, while signed out, as AUTH_FAIL does)
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

return await new FakeSession(version).RunAsync();

internal sealed class FakeSession(string version)
{
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingFromUs = new();
    private readonly Queue<string> _prompts = new();
    private readonly SemaphoreSlim _promptSignal = new(0);
    private CancellationTokenSource? _turn;
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
        if (subtype is "claude_authenticate" or "claude_oauth_wait_for_completion" or "claude_oauth_callback")
        {
            await HandleSignInAsync(requestId, subtype, message["request"]!.AsObject());
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
            "get_usage" => Usage(),
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
            await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = _sessionId, ["model"] = "claude-fake-1", ["permissionMode"] = "default", ["claude_code_version"] = version, ["capabilities"] = new JsonArray("interrupt_receipt_v1") });
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

    private Task DeltaAsync(string text) => WriteAsync(new JsonObject
    {
        ["type"] = "stream_event",
        ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } },
        ["parent_tool_use_id"] = null,
    });

    private Task AssistantAsync(JsonObject block) => WriteAsync(new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject { ["id"] = "msg_fake", ["model"] = "claude-fake-1", ["content"] = new JsonArray(block) },
        ["parent_tool_use_id"] = null,
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
