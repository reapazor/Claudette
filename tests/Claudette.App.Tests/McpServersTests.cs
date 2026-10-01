using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.App.Tests;

/// <summary>The side panel's MCP page (DESIGN.md §4, "MCP servers").</summary>
public sealed class McpServersTests : IAsyncDisposable
{
    private readonly ScriptedTransport _transport = new();
    private readonly ClaudeSession _session;
    private JsonArray _servers = [];

    public McpServersTests()
    {
        _session = new ClaudeSession(_transport.ForSession(), new FakeTimeProvider());
        _transport.Answers["mcp_status"] = _ => new JsonObject { ["mcpServers"] = _servers.DeepClone() };
        _transport.Answers["mcp_reconnect"] = request =>
        {
            Set(request["serverName"]!.GetValue<string>(), "connected");
            return [];
        };
        _transport.Answers["mcp_toggle"] = request =>
        {
            Set(request["serverName"]!.GetValue<string>(), request["enabled"]!.GetValue<bool>() ? "connected" : "disabled");
            return [];
        };
    }

    private void Set(string name, string status)
    {
        foreach (var server in _servers.OfType<JsonObject>().Where(s => s["name"]!.GetValue<string>() == name))
        {
            server["status"] = status;
            server.Remove("error");
        }
    }

    [Fact]
    public async Task Servers_are_listed_by_name_and_failures_call_for_attention()
    {
        _servers = JsonNode.Parse("""
            [{"name":"tickets","status":"connected","scope":"project","serverInfo":{"name":"t","version":"1.2.0"},"tools":[{"name":"a"},{"name":"b"}]},
             {"name":"Auth","status":"needs-auth"},
             {"name":"broken","status":"failed","error":"spawn ENOENT"}]
            """)!.AsArray();
        var page = new McpServersViewModel(() => _session);

        await page.RefreshAsync();

        Assert.Equal(["Auth", "broken", "tickets"], page.Servers.Select(s => s.Name));
        Assert.Equal(["Needs signing in", "Failed", "Connected · 2 tools"], page.Servers.Select(s => s.StateText));
        Assert.Equal("project · v1.2.0", page.Servers[2].Detail);
        Assert.Equal("spawn ENOENT", page.Servers[1].Error);
        Assert.Equal("2 need attention", page.Attention);
    }

    [Fact]
    public async Task Reconnecting_and_turning_off_ask_Claude_Code_and_read_the_list_again()
    {
        _servers = JsonNode.Parse("""[{"name":"broken","status":"failed"},{"name":"tickets","status":"connected"}]""")!.AsArray();
        var page = new McpServersViewModel(() => _session);
        await page.RefreshAsync();

        await page.ReconnectCommand.ExecuteAsync(page.Servers[0]);
        Assert.True(page.Servers[0].IsConnected);
        Assert.Null(page.Attention);

        await page.ToggleCommand.ExecuteAsync(page.Servers[1]);
        Assert.Equal("Off", page.Servers[1].StateText);
        await page.ToggleCommand.ExecuteAsync(page.Servers[1]);
        Assert.True(page.Servers[1].IsConnected);
    }

    [Fact]
    public async Task Without_a_session_there_is_nothing_to_show()
    {
        var page = new McpServersViewModel(() => null);

        await page.RefreshAsync();

        Assert.True(page.IsEmpty);
    }

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}
