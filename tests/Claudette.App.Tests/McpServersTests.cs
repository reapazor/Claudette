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
    public async Task Sign_in_opens_the_page_Claude_Code_gives_and_takes_the_address_the_browser_ended_on()
    {
        // As Claude Code 2.1.286 answers mcp_authenticate for a server with OAuth, waiting on this machine.
        _servers = JsonNode.Parse("""[{"name":"linear","status":"needs-auth","config":{"type":"http","url":"https://mcp.example.com"}}]""")!.AsArray();
        _transport.Answers["mcp_authenticate"] = _ => new JsonObject
        {
            ["authUrl"] = "https://auth.example.com/authorize?state=s1", ["requiresUserAction"] = true, ["callbackExpected"] = true,
            ["redirectScheme"] = "localhost", ["state"] = "s1", ["callbackPort"] = 54321,
        };
        _transport.Answers["mcp_oauth_callback_url"] = request =>
        {
            Set(request["serverName"]!.GetValue<string>(), "connected");
            return [];
        };
        var opened = new List<string>();
        var page = new McpServersViewModel(() => _session, url =>
        {
            opened.Add(url);
            return Task.CompletedTask;
        });
        await page.RefreshAsync();
        var row = page.Servers.Single();
        Assert.True(row.CanSignIn);

        await page.SignInCommand.ExecuteAsync(row);

        Assert.Equal(["https://auth.example.com/authorize?state=s1"], opened);
        Assert.True(row.IsSigningIn);
        Assert.True(row.CanPasteAddress);
        Assert.Equal("linear", LastRequest("mcp_authenticate")["serverName"]!.GetValue<string>());

        // The browser couldn't come back to Claude Code: the address it ended on finishes the sign-in.
        row.PastedAddress = "  http://localhost:54321/callback?code=abc&state=s1 ";
        await page.FinishSignInCommand.ExecuteAsync(row);

        Assert.Equal("http://localhost:54321/callback?code=abc&state=s1", LastRequest("mcp_oauth_callback_url")["callbackUrl"]!.GetValue<string>());
        Assert.True(page.Servers.Single().IsConnected);
        // Connected over HTTP, it can be signed out of.
        Assert.True(page.Servers.Single().CanSignOut);
        _transport.Answers["mcp_clear_auth"] = request =>
        {
            Set(request["serverName"]!.GetValue<string>(), "needs-auth");
            return [];
        };
        await page.SignOutCommand.ExecuteAsync(page.Servers.Single());
        Assert.True(page.Servers.Single().CanSignIn);
    }

    [Fact]
    public async Task A_server_signed_in_already_is_read_again_and_a_refused_sign_in_points_to_the_terminal()
    {
        _servers = JsonNode.Parse("""[{"name":"stdio-tool","status":"needs-auth","config":{"type":"stdio","command":"x"}}]""")!.AsArray();
        _transport.Answers["mcp_authenticate"] = _ =>
        {
            Set("stdio-tool", "connected");
            return new JsonObject { ["requiresUserAction"] = false, ["callbackExpected"] = false };
        };
        var page = new McpServersViewModel(() => _session, _ => throw new InvalidOperationException("nothing to open"));
        await page.RefreshAsync();

        await page.SignInCommand.ExecuteAsync(page.Servers.Single());
        Assert.True(page.Servers.Single().IsConnected);
        // A stdio server has no sign-in to forget.
        Assert.False(page.Servers.Single().CanSignOut);

        Set("stdio-tool", "needs-auth");
        await page.RefreshAsync();
        _transport.Answers["mcp_authenticate"] = _ => throw new InvalidOperationException("Server type \"stdio\" does not support OAuth authentication");
        await page.SignInCommand.ExecuteAsync(page.Servers.Single());
        Assert.StartsWith("Couldn't sign in to stdio-tool: ", page.Error, StringComparison.Ordinal);
        Assert.Contains("does not support OAuth authentication", page.Error, StringComparison.Ordinal);
        Assert.EndsWith("You can sign in with /mcp in Claude Code in a terminal.", page.Error, StringComparison.Ordinal);
    }

    private JsonObject LastRequest(string subtype) =>
        _transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == subtype)["request"]!.AsObject();

    [Fact]
    public async Task Without_a_session_there_is_nothing_to_show()
    {
        var page = new McpServersViewModel(() => null);

        await page.RefreshAsync();

        Assert.True(page.IsEmpty);
    }

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}
