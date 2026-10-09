using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Sessions;

/// <summary>The Claude Code sessions running on this machine, from Claude Code's sessions folder (DESIGN.md §13, "Session naming").</summary>
public sealed class LiveSessionsTests : IDisposable
{
    private readonly TempFolder _config = new("claudette-live-sessions");

    public void Dispose() => _config.Dispose();

    // An entry as Claude Code 2.1.284 writes it, for a tab named by Claudette.
    private const string Named = """
        {"pid":47960,"sessionId":"66790aac-38ea-4f5f-96ea-4ba2f713ec83","cwd":"C:\\work\\api","startedAt":1791568495775,"version":"2.1.284",
         "peerProtocol":1,"kind":"interactive","entrypoint":"sdk-cli","messagingSocketPath":"\\\\.\\pipe\\LOCAL\\cc-msg-1d","name":"Docs page",
         "nameSource":"user","status":"idle"}
        """;

    [Fact]
    public void An_entry_gives_the_name_the_session_is_reached_by()
    {
        var session = LiveSessions.Parse(Named);

        Assert.Equal(new LiveSession(47960, "66790aac-38ea-4f5f-96ea-4ba2f713ec83", "Docs page", @"C:\work\api", IsNamed: true, Status: "idle"), session);
    }

    [Fact]
    public void A_name_Claude_Code_made_up_is_not_one_it_was_given() =>
        Assert.False(LiveSessions.Parse("""{"pid":1,"sessionId":"s","name":"api-3f","nameSource":"derived"}""")!.IsNamed);

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"sessionId":"s","name":"n"}""")]
    [InlineData("""{"pid":1,"name":"n"}""")]
    [InlineData("""{"pid":1,"sessionId":"s"}""")]
    [InlineData("""{"pid":"1","sessionId":"s","name":"n"}""")]
    [InlineData("""{"pid":-4,"sessionId":"s","name":"n"}""")]
    public void An_entry_without_what_it_needs_is_skipped(string json) => Assert.Null(LiveSessions.Parse(json));

    [Fact]
    public void The_folder_lists_the_sessions_still_running_by_name()
    {
        _config.Write("sessions/47960.json", Named);
        _config.Write("sessions/100.json", """{"pid":100,"sessionId":"a","name":"api-3f","nameSource":"derived"}""");
        _config.Write("sessions/200.json", """{"pid":200,"sessionId":"b","name":"crashed"}""");
        _config.Write("sessions/300.json", "{ half writ");

        var sessions = LiveSessions.Read(_config.Path, pid => pid != 200);

        Assert.Equal(["api-3f", "Docs page"], sessions.Select(s => s.Name));
    }

    [Fact]
    public void The_messaging_keys_beside_the_entries_are_never_read()
    {
        _config.Write("sessions/100.json", """{"pid":100,"sessionId":"a","name":"api-3f"}""");
        // Shaped like an entry, in case one were ever parsed by mistake.
        _config.Write("sessions/100.c907ea97.key", """{"pid":101,"sessionId":"k","name":"key"}""");
        _config.Write("sessions/notes.json", """{"pid":102,"sessionId":"n","name":"notes"}""");

        Assert.Equal(["api-3f"], LiveSessions.Read(_config.Path, _ => true).Select(s => s.Name));
    }

    [Fact]
    public void Without_the_folder_there_are_none()
    {
        Assert.Empty(LiveSessions.Read(_config.Path, _ => true));
        Assert.Empty(LiveSessions.Read(null, _ => true));
    }
}
