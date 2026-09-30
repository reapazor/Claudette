using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claudette.Core.Protocol;

namespace Claudette.Core.Tests.Support;

/// <summary>
/// Recorded Claude Code protocol traffic (DESIGN.md §15, "Protocol fixtures"). Each line is
/// <c>{"dir":"out","msg":…}</c> (Claude Code wrote it), <c>{"dir":"in","msg":…}</c> (the host sent it) or
/// <c>{"dir":"exit","code":…}</c>.
/// </summary>
internal sealed record ProtocolFixture(string Name, IReadOnlyList<FixtureEntry> Entries)
{
    public const string DefaultVersion = "2.1.284";

    public IEnumerable<string> OutputLines => Entries.Where(e => e.Direction == "out").Select(e => e.Message!.ToJsonString());

    public static ProtocolFixture Load(string name, string version = DefaultVersion)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "protocol", version, $"{name}.jsonl");
        var entries = File.ReadLines(path)
            .Where(l => l.Length > 0)
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .Select(o => new FixtureEntry(o["dir"]!.GetValue<string>(), o["msg"]?.AsObject(), o["code"]?.GetValue<int>()))
            .ToArray();
        return new ProtocolFixture(name, entries);
    }

    /// <summary>Every version with recordings: <see cref="DefaultVersion"/>'s full set, and later versions' re-recorded scenarios.</summary>
    public static IEnumerable<string> AllVersions() =>
        Directory.EnumerateDirectories(Path.Combine(AppContext.BaseDirectory, "Fixtures", "protocol"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal);

    public static IEnumerable<string> AllNames(string version = DefaultVersion) =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "protocol", version), "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order();
}

internal sealed record FixtureEntry(string Direction, JsonObject? Message, int? ExitCode);

/// <summary>
/// Plays a fixture back as Claude Code. Output lines are emitted in order; at each recorded host line, playback waits
/// until the session under test sends its next line. Control request ids line up because both the recording and
/// <see cref="ClaudeSession"/> number requests <c>req_1</c>, <c>req_2</c>, … in send order.
/// </summary>
internal sealed class ReplayTransport : IClaudeTransport
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<TransportExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _sentSignal = new(0);
    private readonly List<JsonObject> _sent = [];

    public ReplayTransport(ProtocolFixture fixture)
    {
        _ = PlayAsync(fixture);
    }

    public ChannelReader<string> Output => _output.Reader;

    public Task<TransportExit> Completion => _completion.Task;

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

    public ValueTask SendAsync(string line, CancellationToken cancellationToken = default)
    {
        lock (_sent)
        {
            _sent.Add(JsonNode.Parse(line)!.AsObject());
        }
        _sentSignal.Release();
        return ValueTask.CompletedTask;
    }

    public void CloseInput() => _sentSignal.Release(1000);

    public void Terminate() => Finish(-1);

    public ValueTask DisposeAsync()
    {
        Finish(0);
        return ValueTask.CompletedTask;
    }

    private async Task PlayAsync(ProtocolFixture fixture)
    {
        foreach (var entry in fixture.Entries)
        {
            switch (entry.Direction)
            {
                case "out":
                    _output.Writer.TryWrite(entry.Message!.ToJsonString());
                    break;
                case "in":
                    if (!await _sentSignal.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                    {
                        Finish(-2, $"Replay of '{fixture.Name}' timed out waiting for the host to send: {entry.Message!.ToJsonString()}");
                        return;
                    }
                    break;
                case "exit":
                    Finish(entry.ExitCode ?? 0);
                    return;
            }
        }
        Finish(0);
    }

    private void Finish(int code, string stderr = "")
    {
        _output.Writer.TryComplete();
        _completion.TrySetResult(new TransportExit(code, stderr));
    }
}
