using System.Text.Json.Nodes;
using Claudette.Fixtures;

namespace Claudette.IntegrationTests;

/// <summary>Record mode's cleaning of a protocol log into a fixture (DESIGN.md §15, "Protocol fixtures").</summary>
public class ProtocolFixtureWriterTests
{
    [Fact]
    public void A_log_becomes_fixture_lines_without_paths_emails_accounts_or_session_ids()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\Temp\claudette-record-1234" : "/tmp/claudette-record-1234";
        var repo = Path.Combine(root, "repo");
        var project = Path.Combine(root, "config", "projects", repo.Replace('/', '-').Replace('\\', '-').Replace(':', '-'));
        const string session = "3a26fa01-7245-409f-8157-b991380c5155";
        var log = new[]
        {
            Log('>', new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = $"hi from {repo}" }, ["session_id"] = "" }),
            Log('<', new JsonObject
            {
                ["type"] = "system", ["subtype"] = "init", ["cwd"] = repo, ["session_id"] = session,
                ["account"] = new JsonObject { ["email"] = "matt@example.org", ["organization"] = "Acme" },
            }),
            Log('<', new JsonObject { ["type"] = "user", ["tool_use_result"] = new JsonObject { ["path"] = Path.Combine(project, $"{session}.jsonl") }, ["session_id"] = session }),
            Log('<', new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Mail matt@example.org <ok>" }) } }),
            "not a log line",
        };

        var lines = new ProtocolFixtureWriter([root]).FromProtocolLog(log).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();

        Assert.Equal(["in", "out", "out", "out"], lines.Select(l => l["dir"]!.GetValue<string>()));
        var init = lines[1]["msg"]!;
        Assert.Equal(Path.Combine("<ROOT>", "repo"), init["cwd"]!.GetValue<string>());
        Assert.Equal("session-1", init["session_id"]!.GetValue<string>());
        Assert.Equal("<redacted>", init["account"]!["email"]!.GetValue<string>());
        Assert.Equal("<redacted>", init["account"]!["organization"]!.GetValue<string>());
        Assert.EndsWith("session-1.jsonl", lines[2]["msg"]!["tool_use_result"]!["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("claudette-record", lines[2].ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("Mail user@example.com <ok>", lines[3]["msg"]!["message"]!["content"]![0]!["text"]!.GetValue<string>());
        // Readable, like Claude Code's own output.
        Assert.Contains("<ok>", new ProtocolFixtureWriter([root]).FromProtocolLog(log)[3], StringComparison.Ordinal);
    }

    private static string Log(char direction, JsonObject message) => $"2026-09-29T10:00:00.000Z {direction} {message.ToJsonString()}";
}
