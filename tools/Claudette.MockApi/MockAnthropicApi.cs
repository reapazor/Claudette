using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Claudette.MockApi;

/// <summary>A request the mock received, for tests to check what reached the API.</summary>
/// <param name="LastUserImages">The images in the latest user message, in order.</param>
public sealed record RecordedRequest(
    string Method,
    string Path,
    string? Model,
    string? Effort,
    int ToolCount,
    int MessageCount,
    string LastUserText,
    string Reply,
    string LastToolResultText = "",
    IReadOnlyList<RecordedImage>? LastUserImages = null);

/// <summary>An image that reached the API: its media type, size, and for a PNG its dimensions.</summary>
public sealed record RecordedImage(string MediaType, int Bytes, int? Width, int? Height);

/// <summary>
/// A fake Anthropic Messages API, so the real <c>claude</c> CLI can be driven without tokens (DESIGN.md §15,
/// "Real CLI against a fake model"). Point Claude Code at <see cref="BaseAddress"/> with <c>ANTHROPIC_BASE_URL</c>
/// and any <c>ANTHROPIC_API_KEY</c>. Replies are scripted from keywords in the latest prompt:
/// <list type="bullet">
/// <item><c>WRITE_FILE &lt;path&gt;</c>: a Write tool call, then "Done with the tool."</item>
/// <item><c>EDIT_FILE &lt;path&gt;</c>: a Read, then an Edit replacing <c>ORIGINAL LINE</c> with <c>EDITED LINE</c>, then done.</item>
/// <item><c>RUN_BASH &lt;command&gt;</c>: a Bash tool call, then done.</item>
/// <item><c>SLOW</c>: text streamed in small chunks over about 20 seconds.</item>
/// <item><c>ASK_QUESTION</c>: an AskUserQuestion tool call ("Which database?": Postgres or SQLite), then done.</item>
/// <item><c>EXIT_PLAN</c>: an ExitPlanMode tool call with a two-step plan, then done. Needs plan mode.</item>
/// <item><c>API_ERROR</c>: the first two requests fail with 529 "overloaded", so Claude Code retries; then <c>pong</c>.</item>
/// <item>Requests with no tools that mention "title": <c>{"title": "Mock session title"}</c>.</item>
/// <item>Anything else: <c>pong</c>.</item>
/// </list>
/// Ported from <c>spikes/mock-server.mjs</c>.
/// </summary>
public sealed partial class MockAnthropicApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _counter;
    private int _apiErrors;

    private MockAnthropicApi(WebApplication app)
    {
        _app = app;
    }

    public Uri BaseAddress { get; private set; } = null!;

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>Starts the mock on 127.0.0.1. Port 0 picks a free port.</summary>
    public static async Task<MockAnthropicApi> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        var mock = new MockAnthropicApi(app);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        mock.BaseAddress = new Uri(address);
        return mock;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "";
        JsonObject body = [];
        if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                body = await JsonNode.ParseAsync(request.Body) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                // Not JSON.
            }
        }

        if (path.StartsWith("/v1/messages/count_tokens", StringComparison.Ordinal))
        {
            Record(request.Method, path, body, "count_tokens");
            await context.Response.WriteAsJsonAsync(new { input_tokens = 100 });
            return;
        }
        if (!path.StartsWith("/v1/messages", StringComparison.Ordinal))
        {
            Record(request.Method, path, body, "not_found");
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "not_found_error", message = "mock: not implemented" } });
            return;
        }

        // API_ERROR: overloaded twice, so Claude Code reports its retries (system/api_retry), then a normal reply.
        if (LastUserText(body).Text.Contains("API_ERROR", StringComparison.Ordinal) && Interlocked.Increment(ref _apiErrors) <= 2)
        {
            Record(request.Method, path, body, "overloaded");
            context.Response.StatusCode = 529;
            await context.Response.WriteAsJsonAsync(new { type = "error", error = new { type = "overloaded_error", message = "mock: overloaded" } });
            return;
        }

        var plan = Script(body);
        Record(request.Method, path, body, plan.Kind);
        var id = $"msg_mock_{Interlocked.Increment(ref _counter)}";
        var stopReason = plan.Blocks.Any(b => b["type"]!.GetValue<string>() == "tool_use") ? "tool_use" : "end_turn";
        var usage = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 20, ["cache_creation_input_tokens"] = 0, ["cache_read_input_tokens"] = 0 };

        if (body["stream"]?.GetValue<bool>() != true)
        {
            await context.Response.WriteAsync(new JsonObject
            {
                ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["model"] = body["model"]?.DeepClone(),
                ["content"] = new JsonArray(plan.Blocks.Select(b => (JsonNode?)b.DeepClone()).ToArray()),
                ["stop_reason"] = stopReason, ["stop_sequence"] = null, ["usage"] = usage,
            }.ToJsonString());
            return;
        }

        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var aborted = context.RequestAborted;
        var startUsage = usage.DeepClone();
        startUsage["output_tokens"] = 1;
        await SseAsync(response, "message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["model"] = body["model"]?.DeepClone(),
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null, ["usage"] = startUsage,
            },
        }, aborted);
        for (var i = 0; i < plan.Blocks.Count; i++)
        {
            var block = plan.Blocks[i];
            if (block["type"]!.GetValue<string>() == "text")
            {
                await SseAsync(response, "content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = i, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } }, aborted);
                var text = block["text"]!.GetValue<string>();
                var chunks = plan.ChunkDelay is null ? [text] : text.Chunk(5).Select(c => new string(c)).ToArray();
                foreach (var chunk in chunks)
                {
                    if (aborted.IsCancellationRequested)
                    {
                        return;
                    }
                    await SseAsync(response, "content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = i, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = chunk } }, aborted);
                    if (plan.ChunkDelay is { } delay)
                    {
                        try
                        {
                            await Task.Delay(delay, aborted);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
            else
            {
                await SseAsync(response, "content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start", ["index"] = i,
                    ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = block["id"]!.DeepClone(), ["name"] = block["name"]!.DeepClone(), ["input"] = new JsonObject() },
                }, aborted);
                await SseAsync(response, "content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta", ["index"] = i,
                    ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = block["input"]!.ToJsonString() },
                }, aborted);
            }
            await SseAsync(response, "content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = i }, aborted);
        }
        await SseAsync(response, "message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null }, ["usage"] = new JsonObject { ["output_tokens"] = 20 } }, aborted);
        await SseAsync(response, "message_stop", new JsonObject { ["type"] = "message_stop" }, aborted);
    }

    private static async Task SseAsync(HttpResponse response, string eventName, JsonObject data, CancellationToken cancellationToken)
    {
        try
        {
            await response.WriteAsync($"event: {eventName}\ndata: {data.ToJsonString()}\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The client went away (for example after an interrupt).
        }
    }

    private void Record(string method, string path, JsonObject body, string reply)
    {
        _requests.Enqueue(new RecordedRequest(
            method,
            path,
            body["model"]?.GetValue<string>(),
            body["output_config"]?["effort"]?.GetValue<string>(),
            (body["tools"] as JsonArray)?.Count ?? 0,
            (body["messages"] as JsonArray)?.Count ?? 0,
            LastUserText(body).Text,
            reply,
            LastToolResultText(body),
            LastUserImages(body)));
    }

    private sealed record Plan(string Kind, List<JsonObject> Blocks, TimeSpan? ChunkDelay = null);

    private Plan Script(JsonObject body)
    {
        var system = body["system"]?.ToJsonString() ?? "";
        var (text, hasToolResult) = LastUserText(body);
        var tools = (body["tools"] as JsonArray)?.OfType<JsonObject>().Select(t => t["name"]?.GetValue<string>()).ToHashSet() ?? [];

        if (tools.Count == 0 && TitlePattern().IsMatch(system + text))
        {
            return new Plan("title", [TextBlock("""{"title": "Mock session title"}""")]);
        }

        // EDIT_FILE needs two tool rounds (Claude Code requires a Read before an Edit), so count the tool rounds
        // since the prompt that asked for it.
        var messages = (body["messages"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var promptIndex = Array.FindLastIndex(messages, m => m["role"]?.GetValue<string>() == "user" && KeywordPattern().IsMatch(MessageText(m)));
        var prompt = promptIndex >= 0 ? MessageText(messages[promptIndex]) : "";
        var toolRounds = promptIndex >= 0 ? messages.Skip(promptIndex + 1).Count(m => m["role"]?.GetValue<string>() == "user") : 0;
        if (EditPattern().Match(prompt) is { Success: true } edit && tools.Contains("Edit"))
        {
            var filePath = edit.Groups[1].Value;
            if (toolRounds == 0)
            {
                return new Plan("tool-read", [ToolUse("Read", new JsonObject { ["file_path"] = filePath })]);
            }
            if (toolRounds == 1)
            {
                return new Plan("tool-edit", [ToolUse("Edit", new JsonObject { ["file_path"] = filePath, ["old_string"] = "ORIGINAL LINE", ["new_string"] = "EDITED LINE" })]);
            }
        }

        if (hasToolResult)
        {
            return new Plan("after-tool", [TextBlock("Done with the tool.")]);
        }
        if (WritePattern().Match(text) is { Success: true } write && tools.Contains("Write"))
        {
            return new Plan("tool", [ToolUse("Write", new JsonObject { ["file_path"] = write.Groups[1].Value, ["content"] = "hello from mock\n" })]);
        }
        if (BashPattern().Match(text) is { Success: true } bash && tools.Contains("Bash"))
        {
            return new Plan("tool", [ToolUse("Bash", new JsonObject { ["command"] = bash.Groups[1].Value.Trim(), ["description"] = "mock command" })]);
        }
        if (text.Contains("ASK_QUESTION", StringComparison.Ordinal) && tools.Contains("AskUserQuestion"))
        {
            var question = new JsonObject
            {
                ["question"] = "Which database?", ["header"] = "Database", ["multiSelect"] = false,
                ["options"] = new JsonArray(
                    new JsonObject { ["label"] = "Postgres", ["description"] = "Relational" },
                    new JsonObject { ["label"] = "SQLite", ["description"] = "A single file" }),
            };
            return new Plan("tool-ask", [ToolUse("AskUserQuestion", new JsonObject { ["questions"] = new JsonArray(question) })]);
        }
        if (text.Contains("EXIT_PLAN", StringComparison.Ordinal) && tools.Contains("ExitPlanMode"))
        {
            return new Plan("tool-plan", [ToolUse("ExitPlanMode", new JsonObject { ["plan"] = "1. Read the code\n2. Fix the bug" })]);
        }
        if (text.Contains("SLOW", StringComparison.Ordinal))
        {
            return new Plan("slow", [TextBlock(string.Concat(Enumerable.Repeat("slow ", 40)))], TimeSpan.FromMilliseconds(500));
        }
        return new Plan("text", [TextBlock("pong")]);
    }

    private JsonObject ToolUse(string name, JsonObject input) =>
        new() { ["type"] = "tool_use", ["id"] = $"toolu_mock_{Interlocked.Increment(ref _counter)}", ["name"] = name, ["input"] = input };

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static (string Text, bool HasToolResult) LastUserText(JsonObject body)
    {
        var messages = (body["messages"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var last = messages.LastOrDefault(m => m["role"]?.GetValue<string>() == "user");
        var hasToolResult = last?["content"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(b => b["type"]?.GetValue<string>() == "tool_result");
        return (last is null ? "" : MessageText(last), hasToolResult);
    }

    /// <summary>The text of the tool results in the latest user message, so tests can see what a tool returned.</summary>
    private static string LastToolResultText(JsonObject body)
    {
        var messages = (body["messages"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var last = messages.LastOrDefault(m => m["role"]?.GetValue<string>() == "user");
        if (last?["content"] is not JsonArray blocks)
        {
            return "";
        }
        return string.Join("\n", blocks.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_result").Select(b => b["content"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>())),
            _ => "",
        }));
    }

    /// <summary>The base64 image blocks in the latest user message.</summary>
    private static IReadOnlyList<RecordedImage> LastUserImages(JsonObject body)
    {
        var last = (body["messages"] as JsonArray)?.OfType<JsonObject>().LastOrDefault(m => m["role"]?.GetValue<string>() == "user");
        if (last?["content"] is not JsonArray blocks)
        {
            return [];
        }
        return blocks.OfType<JsonObject>()
            .Where(b => b["type"]?.GetValue<string>() == "image")
            .Select(b =>
            {
                var mediaType = b["source"]?["media_type"]?.GetValue<string>() ?? "";
                byte[] data;
                try
                {
                    data = Convert.FromBase64String(b["source"]?["data"]?.GetValue<string>() ?? "");
                }
                catch (FormatException)
                {
                    data = [];
                }
                // A PNG's width and height are the first fields of its IHDR chunk, at bytes 16 and 20.
                var isPng = data.Length >= 24 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';
                return new RecordedImage(
                    mediaType,
                    data.Length,
                    isPng ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)) : null,
                    isPng ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)) : null);
            })
            .ToArray();
    }

    /// <summary>A message's plain text: its string content, or its text blocks joined.</summary>
    private static string MessageText(JsonObject message) => message["content"] switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "text").Select(b => b["text"]?.GetValue<string>())),
        _ => "",
    };

    [GeneratedRegex("title", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePattern();

    [GeneratedRegex("EDIT_FILE|WRITE_FILE|RUN_BASH|SLOW|ASK_QUESTION|EXIT_PLAN|hello")]
    private static partial Regex KeywordPattern();

    [GeneratedRegex(@"EDIT_FILE (\S+)")]
    private static partial Regex EditPattern();

    [GeneratedRegex(@"WRITE_FILE (\S+)")]
    private static partial Regex WritePattern();

    [GeneratedRegex(@"RUN_BASH (.+)")]
    private static partial Regex BashPattern();
}
