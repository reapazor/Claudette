using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.Core;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.App.Tests.Support;

/// <summary>Plays Claude Code's side of one session for view model tests.</summary>
internal sealed class ScriptedTransport : IClaudeTransport
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<TransportExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<JsonObject> _sent = [];

    /// <summary>Answers to control requests by subtype; null leaves the request unanswered, an exception answers with an error.</summary>
    public Dictionary<string, Func<JsonObject, JsonObject?>> Answers { get; } = new()
    {
        ["initialize"] = _ => new JsonObject
        {
            ["models"] = new JsonArray(
                new JsonObject { ["value"] = "default", ["resolvedModel"] = "claude-opus-5-5", ["displayName"] = "Default (recommended)", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high") },
                new JsonObject { ["value"] = "opus", ["resolvedModel"] = "claude-opus-5-5", ["displayName"] = "Opus", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high") },
                new JsonObject { ["value"] = "haiku", ["resolvedModel"] = "claude-haiku-4-5", ["displayName"] = "Haiku", ["supportsEffort"] = false }),
            ["current_permission_mode"] = "default",
        },
        ["get_context_usage"] = _ => new JsonObject { ["totalTokens"] = 1000, ["maxTokens"] = 200000, ["percentage"] = 0.5 },
    };

    public IReadOnlyList<JsonObject> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public IEnumerable<string> SentUserTexts => Sent.Where(m => m["type"]?.GetValue<string>() == "user").Select(m => m["message"]!["content"]!.GetValue<string>());

    public IEnumerable<string> SentControlSubtypes => Sent.Where(m => m["type"]?.GetValue<string>() == "control_request").Select(m => m["request"]!["subtype"]!.GetValue<string>());

    public ChannelReader<string> Output => _output.Reader;

    public Task<TransportExit> Completion => _completion.Task;

    public ValueTask SendAsync(string line, CancellationToken cancellationToken = default)
    {
        var message = JsonNode.Parse(line)!.AsObject();
        lock (_sent)
        {
            _sent.Add(message);
        }
        if (message["type"]?.GetValue<string>() == "control_request")
        {
            var id = message["request_id"]!.GetValue<string>();
            var request = message["request"]!.AsObject();
            var subtype = request["subtype"]!.GetValue<string>();
            if (Answers.TryGetValue(subtype, out var answer))
            {
                try
                {
                    if (answer(request) is { } response)
                    {
                        Emit(OutgoingMessages.ControlSuccess(id, response));
                    }
                }
                catch (Exception ex)
                {
                    Emit(OutgoingMessages.ControlError(id, ex.Message));
                }
            }
            else
            {
                Emit(OutgoingMessages.ControlSuccess(id, []));
            }
        }
        return ValueTask.CompletedTask;
    }

    public void Emit(JsonObject message) => _output.Writer.TryWrite(message.ToJsonString());

    public void Emit(string line) => _output.Writer.TryWrite(line);

    /// <summary>A complete turn: init, a text reply and a result with usage.</summary>
    public void EmitTurn(string reply = "ok", string model = "claude-opus-5-5")
    {
        Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = model, ["permissionMode"] = "default" });
        Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply }) },
        });
        Emit(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = reply,
            ["session_id"] = "s1",
            ["modelUsage"] = new JsonObject
            {
                [model] = new JsonObject { ["inputTokens"] = 100, ["outputTokens"] = 20, ["cacheReadInputTokens"] = 0, ["cacheCreationInputTokens"] = 0, ["costUSD"] = 0.01 },
            },
        });
    }

    public void CloseInput() => Exit(0);

    public void Terminate() => Exit(-1);

    public void Exit(int code)
    {
        _output.Writer.TryComplete();
        _completion.TrySetResult(new TransportExit(code, ""));
    }

    public ValueTask DisposeAsync()
    {
        Exit(0);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScriptedSessionFactory(ScriptedTransport transport, TimeProvider time) : IClaudeSessionFactory
{
    public List<ClaudeLaunchOptions> Launches { get; } = [];

    public async Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default)
    {
        Launches.Add(options);
        var session = new ClaudeSession(transport, time);
        await session.InitializeAsync(cancellationToken);
        return session;
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    private readonly Lock _lock = new();

    public void Post(Action action)
    {
        // Serialize like a UI thread would.
        lock (_lock)
        {
            action();
        }
    }
}

internal sealed class NoPlatform : IPlatformServices
{
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);

    public Task OpenUrlAsync(string url) => Task.CompletedTask;

    public Task RevealFolderAsync(string path) => Task.CompletedTask;

    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
}

/// <summary>A tab wired to a scripted Claude Code, with a fake clock and a temporary data folder.</summary>
internal sealed class TabTestHarness : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-tabtest-{Guid.NewGuid():N}");

    public TabTestHarness()
    {
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        Services = new AppServices(AppPaths.Under(_root), new ProcessLauncher(), Time, new NoPlatform(), new InlineDispatcher());
        Factory = new ScriptedSessionFactory(Transport, Time);
        Services.UseSessionFactory(Factory);
        Shell = new ShellViewModel(Services, () => { });
    }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));

    public ScriptedTransport Transport { get; } = new();

    public ScriptedSessionFactory Factory { get; }

    public AppServices Services { get; }

    public ShellViewModel Shell { get; }

    public string WorkFolder => Path.Combine(_root, "work");

    public async Task<TabViewModel> OpenTabAsync()
    {
        await Shell.OpenFolderAsync(WorkFolder);
        var tab = Shell.SelectedTab!;
        await Eventually(() => tab.Status == TabStatus.Idle);
        return tab;
    }

    public static async Task Eventually(Func<bool> condition, string? what = null)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {what ?? "the condition"}.");
    }

    public async ValueTask DisposeAsync()
    {
        await Shell.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class SettingsExtensions
{
    public static QuickSuffix Suffix(this AppServices services, string id) => services.Settings.QuickSuffixes.Single(s => s.Id == id);
}
