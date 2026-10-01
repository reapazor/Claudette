using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Protocol;

/// <summary>Protocol logs and the counts on Settings → Advanced → Diagnostics (DESIGN.md §13, "Logging"; §16).</summary>
public class ProtocolLoggingTests
{
    [Fact]
    public async Task Diagnostics_count_unknown_types_new_fields_and_unreadable_lines()
    {
        var diagnostics = new ProtocolDiagnostics();
        var transport = new FakeTransport();
        await using var session = new ClaudeSession(transport, new FakeTimeProvider(), diagnostics: diagnostics);

        transport.Emit("""{"type":"hologram","payload":1}""");
        transport.Emit("""{"type":"hologram","payload":2}""");
        transport.Emit("""{"type":"result","subtype":"success","session_id":"s","is_error":false,"result":"ok","sparkle_level":3}""");
        transport.Emit("""{"type":"system","subtype":"status","status":"idle","session_id":"s","mood":"calm"}""");
        transport.Emit("""{"type":"rate_limit_event","anything":"goes"}""");
        transport.Emit("not json at all");
        transport.Exit();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(new Dictionary<string, int> { ["hologram"] = 2 }, snapshot.UnknownMessageTypes);
        Assert.Equal(2, snapshot.UnknownMessageCount);
        // Types without a known field list aren't checked.
        Assert.Equal(new Dictionary<string, int> { ["result.sparkle_level"] = 1, ["system/status.mood"] = 1 }, snapshot.UnknownFields);
        Assert.Equal(1, snapshot.ParseErrors);
    }

    [Fact]
    public async Task A_logging_transport_writes_both_directions_with_the_time()
    {
        using var temp = new TempFolder();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var path = temp.Combine("logs", ProtocolLog.FileName(time.GetUtcNow(), "api / tab"));
        var inner = new FakeTransport();
        var transport = new LoggingTransport(inner, new ProtocolLog(path, time));

        await transport.SendAsync("""{"type":"user"}""", TestContext.Current.CancellationToken);
        inner.Emit("""{"type":"result"}""");
        Assert.Equal("""{"type":"result"}""", await transport.Output.ReadAsync(TestContext.Current.CancellationToken));
        await transport.DisposeAsync();

        Assert.EndsWith("20260929-100000-api---tab.log", path, StringComparison.Ordinal);
        Assert.Equal(
            ["""2026-09-29T10:00:00.000Z > {"type":"user"}""", """2026-09-29T10:00:00.000Z < {"type":"result"}"""],
            await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Two_logs_with_the_same_name_dont_collide()
    {
        // Two tabs in the same folder restored in the same second: the second used to fail to open its log.
        using var temp = new TempFolder();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var path = temp.Combine("logs", ProtocolLog.FileName(time.GetUtcNow(), "api"));

        using var first = new ProtocolLog(path, time);
        using var second = new ProtocolLog(path, time);
        using var third = new ProtocolLog(path, time);

        Assert.Equal(path, first.Path);
        Assert.EndsWith("20260929-100000-api-2.log", second.Path, StringComparison.Ordinal);
        Assert.EndsWith("20260929-100000-api-3.log", third.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_logs_are_deleted()
    {
        using var temp = new TempFolder();
        var now = DateTimeOffset.Parse("2026-09-29T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var old = temp.Combine("old.log");
        var recent = temp.Combine("recent.log");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTimeUtc(old, (now - ProtocolLog.KeepFor - TimeSpan.FromHours(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, (now - TimeSpan.FromHours(1)).UtcDateTime);

        ProtocolLog.DeleteOld(temp.Path, now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
