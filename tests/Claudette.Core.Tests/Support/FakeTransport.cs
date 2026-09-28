using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claudette.Core.Protocol;

namespace Claudette.Core.Tests.Support;

/// <summary>A scriptable <see cref="IClaudeTransport"/>: the test plays Claude Code's side.</summary>
internal sealed class FakeTransport : IClaudeTransport
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<TransportExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _sent = [];
    private readonly List<(Func<JsonObject, bool> Match, TaskCompletionSource<JsonObject> Done)> _sentWaiters = [];

    /// <summary>Automatic answers to control requests, by subtype. The default answers <c>initialize</c> with <see cref="InitializeResponse"/>.</summary>
    public Dictionary<string, Func<JsonObject, JsonObject?>> AutoRespond { get; } = new();

    public JsonObject InitializeResponse { get; set; } = new()
    {
        ["models"] = new JsonArray(new JsonObject
        {
            ["value"] = "sonnet",
            ["displayName"] = "Sonnet",
            ["supportsEffort"] = true,
            ["supportedEffortLevels"] = new JsonArray("low", "medium", "high"),
        }),
        ["commands"] = new JsonArray(),
        ["account"] = new JsonObject { ["subscriptionType"] = "Claude Max" },
        ["current_permission_mode"] = "default",
    };

    public bool InputClosed { get; private set; }

    public bool Terminated { get; private set; }

    public ChannelReader<string> Output => _output.Reader;

    public Task<TransportExit> Completion => _completion.Task;

    public IReadOnlyList<JsonObject> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.Select(s => JsonNode.Parse(s)!.AsObject()).ToArray();
            }
        }
    }

    public ValueTask SendAsync(string line, CancellationToken cancellationToken = default)
    {
        var obj = JsonNode.Parse(line)!.AsObject();
        List<TaskCompletionSource<JsonObject>> matched = [];
        lock (_sent)
        {
            _sent.Add(line);
            foreach (var waiter in _sentWaiters.ToArray())
            {
                if (waiter.Match(obj))
                {
                    _sentWaiters.Remove(waiter);
                    matched.Add(waiter.Done);
                }
            }
        }
        foreach (var done in matched)
        {
            done.TrySetResult(obj);
        }

        if (obj["type"]?.GetValue<string>() == "control_request")
        {
            var requestId = obj["request_id"]!.GetValue<string>();
            var request = obj["request"]!.AsObject();
            var subtype = request["subtype"]!.GetValue<string>();
            if (AutoRespond.TryGetValue(subtype, out var respond))
            {
                if (respond(request) is { } response)
                {
                    EmitJson(OutgoingMessages.ControlSuccess(requestId, response));
                }
            }
            else if (subtype == "initialize")
            {
                EmitJson(OutgoingMessages.ControlSuccess(requestId, InitializeResponse));
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Waits until Claudette sends a line matching <paramref name="match"/> (including one already sent).</summary>
    public Task<JsonObject> WaitForSentAsync(Func<JsonObject, bool> match)
    {
        lock (_sent)
        {
            foreach (var line in _sent)
            {
                var obj = JsonNode.Parse(line)!.AsObject();
                if (match(obj))
                {
                    return Task.FromResult(obj);
                }
            }
            var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sentWaiters.Add((match, done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public void Emit(string line) => _output.Writer.TryWrite(line);

    public void EmitJson(JsonObject message) => Emit(message.ToJsonString());

    public void Exit(int code = 0, string stderr = "")
    {
        _output.Writer.TryComplete();
        _completion.TrySetResult(new TransportExit(code, stderr));
    }

    public void CloseInput()
    {
        InputClosed = true;
        Exit(0);
    }

    public void Terminate()
    {
        Terminated = true;
        Exit(-1);
    }

    public ValueTask DisposeAsync()
    {
        Exit(0);
        return ValueTask.CompletedTask;
    }
}
