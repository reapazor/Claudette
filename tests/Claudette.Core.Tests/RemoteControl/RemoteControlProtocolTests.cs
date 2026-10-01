using System.Text.Json.Nodes;
using Claudette.Core.Auth;
using Claudette.Core.Protocol;
using Claudette.Core.RemoteControl;
using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.RemoteControl;

/// <summary>
/// Remote Control's wire format and replies (DESIGN.md §18): the <c>remote_control</c> request, the <c>/remote-control</c>
/// fallback's replies, <c>bridge_state</c> and <c>worker_shutting_down</c>, whether an account can use it, and the
/// presence file.
/// </summary>
public class RemoteControlProtocolTests
{
    private const string SessionUrl = "https://claude.ai/code/session_01AbCdEf";

    [Fact]
    public async Task The_session_sends_remote_control_requests_as_SDK_hosts_do()
    {
        var transport = new FakeTransport();
        transport.AutoRespond["remote_control"] = request => request.GetBool("enabled") == true
            ? new JsonObject { ["session_url"] = SessionUrl, ["connect_url"] = "https://claude.ai/code?environment=env_1", ["environment_id"] = "env_1", ["bridge_session_id"] = "cse_1", ["bridge_epoch"] = 1 }
            : new JsonObject();
        await using var session = new ClaudeSession(transport, new FakeTimeProvider());
        await session.InitializeAsync(TestContext.Current.CancellationToken);

        var connected = RemoteControlProtocol.FromEnabled(await session.EnableRemoteControlAsync(" Fix the login bug ", TestContext.Current.CancellationToken));
        await session.DisableRemoteControlAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new RemoteControlStatus(RemoteControlState.Connected, SessionUrl), connected);
        var requests = transport.Sent.Where(m => m["request"]?["subtype"]?.GetValue<string>() == "remote_control").Select(m => m["request"]!.ToJsonString()).ToArray();
        Assert.Equal(["""{"subtype":"remote_control","enabled":true,"name":"Fix the login bug"}""", """{"subtype":"remote_control","enabled":false}"""], requests);
    }

    [Fact]
    public void The_answer_gives_the_session_address_or_else_the_connect_address()
    {
        Assert.Equal("https://claude.ai/code?environment=env_1",
            RemoteControlProtocol.FromEnabled(new JsonObject { ["connect_url"] = "https://claude.ai/code?environment=env_1" }).Url);
        // Connected, though Claude Code gave no address to open.
        Assert.Equal(new RemoteControlStatus(RemoteControlState.Connected), RemoteControlProtocol.FromEnabled([]));
        Assert.Null(RemoteControlProtocol.FromEnabled(new JsonObject { ["session_url"] = "javascript:alert(1)" }).Url);
        Assert.Null(RemoteControlProtocol.FromEnabled(new JsonObject { ["session_url"] = 42 }).Url);
    }

    [Theory]
    [InlineData("Unsupported control request subtype: remote_control", true)]
    [InlineData("remote_control is not supported in this context", true)]
    [InlineData("Remote Control requires a claude.ai subscription.", false)]
    public void Only_a_request_Claude_Code_doesnt_know_falls_back_to_the_command(string error, bool unsupported) =>
        Assert.Equal(unsupported, RemoteControlProtocol.IsUnsupported(error));

    [Theory]
    // What 2.1.284 answers in -p mode, whatever the account.
    [InlineData("/remote-control isn't available in this environment.", RemoteControlState.Unavailable)]
    // The troubleshooting page's messages.
    [InlineData("Remote Control requires a claude.ai subscription.", RemoteControlState.Unavailable)]
    [InlineData("Remote Control is disabled by your organization's policy", RemoteControlState.Unavailable)]
    [InlineData("Remote Control isn't enabled for this account", RemoteControlState.Unavailable)]
    [InlineData("Couldn't verify your organization's policy for remote control. Check your network connection and try again.", RemoteControlState.Unavailable)]
    [InlineData("Remote credentials fetch failed", RemoteControlState.Unavailable)]
    [InlineData("Unknown skill: remote-control", RemoteControlState.Unavailable)]
    // An error naming a page on claude.ai isn't a session to open.
    [InlineData("Remote Control is disabled. An Owner can enable it at https://claude.ai/admin-settings/claude-code", RemoteControlState.Unavailable)]
    [InlineData("Remote Control is active. Open https://claude.ai/code/session_01AbCdEf on your phone.", RemoteControlState.Connected)]
    [InlineData("Remote Control connecting…", RemoteControlState.Connected)]
    [InlineData("", RemoteControlState.Unavailable)]
    public void Command_replies_are_read_tolerantly(string reply, RemoteControlState expected)
    {
        var status = RemoteControlProtocol.FromCommandReply(reply);

        Assert.Equal(expected, status.State);
        if (expected == RemoteControlState.Unavailable)
        {
            Assert.False(string.IsNullOrEmpty(status.Detail));
        }
    }

    [Fact]
    public void A_connected_reply_keeps_its_address_and_an_unclear_one_its_words()
    {
        Assert.Equal(new RemoteControlStatus(RemoteControlState.Connected, SessionUrl),
            RemoteControlProtocol.FromCommandReply($"Connected: {SessionUrl}."));
        Assert.Equal(new RemoteControlStatus(RemoteControlState.Connected, Detail: "Remote Control connecting…"),
            RemoteControlProtocol.FromCommandReply("  Remote Control connecting…\n"));
        // The outcome says the command didn't run, whatever the words.
        Assert.Equal(RemoteControlState.Unavailable, RemoteControlProtocol.FromCommandReply("Something new", RemoteControlProtocol.UnavailableHeadless).State);
    }

    [Theory]
    [InlineData("ready", RemoteBridgeState.Ready)]
    [InlineData("connected", RemoteBridgeState.Connected)]
    [InlineData("reconnecting", RemoteBridgeState.Reconnecting)]
    [InlineData("failed", RemoteBridgeState.Failed)]
    [InlineData("policy_disabled", RemoteBridgeState.PolicyDisabled)]
    [InlineData("teleported", RemoteBridgeState.Unknown)]
    [InlineData(null, RemoteBridgeState.Unknown)]
    public void Bridge_states_it_doesnt_know_are_unknown(string? state, RemoteBridgeState expected) =>
        Assert.Equal(expected, RemoteControlProtocol.BridgeState(new JsonObject { ["type"] = "system", ["subtype"] = "bridge_state", ["state"] = state }));

    [Fact]
    public void Bridge_states_move_a_connection_only_where_they_make_sense()
    {
        var connected = new RemoteControlStatus(RemoteControlState.Connected, SessionUrl);
        var reconnecting = RemoteControlProtocol.AfterBridgeState(connected, SessionUrl, Bridge("reconnecting"));
        Assert.Equal(connected with { Detail = RemoteControlProtocol.Reconnecting }, reconnecting);
        Assert.Equal(connected, RemoteControlProtocol.AfterBridgeState(reconnecting!, SessionUrl, Bridge("connected")));
        Assert.Equal(new RemoteControlStatus(RemoteControlState.NotConnected, Detail: "network gone"),
            RemoteControlProtocol.AfterBridgeState(connected, SessionUrl, Bridge("failed", "network gone")));
        Assert.Equal(connected, RemoteControlProtocol.AfterBridgeState(RemoteControlStatus.NotConnected, SessionUrl, Bridge("ready")));
        Assert.Equal(new RemoteControlStatus(RemoteControlState.Unavailable, Detail: "Off by policy."),
            RemoteControlProtocol.AfterBridgeState(RemoteControlStatus.Connecting, null, Bridge("policy_disabled", "Off by policy.")));

        // While connecting, the answer says the rest; a tab that never connected here isn't connected by one.
        Assert.Null(RemoteControlProtocol.AfterBridgeState(RemoteControlStatus.Connecting, null, Bridge("ready")));
        Assert.Null(RemoteControlProtocol.AfterBridgeState(RemoteControlStatus.Connecting, null, Bridge("failed", "no")));
        Assert.Null(RemoteControlProtocol.AfterBridgeState(RemoteControlStatus.NotConnected, null, Bridge("connected")));
        Assert.Null(RemoteControlProtocol.AfterBridgeState(connected, SessionUrl, Bridge("teleported")));
        Assert.Null(RemoteControlProtocol.AfterBridgeState(connected, SessionUrl, Bridge("connected")));
    }

    [Fact]
    public void Worker_shutting_down_disconnects_only_a_connected_tab()
    {
        var connected = new RemoteControlStatus(RemoteControlState.Connected, SessionUrl);
        var message = new JsonObject { ["type"] = "system", ["subtype"] = "worker_shutting_down", ["reason"] = "host_exit" };

        Assert.Equal(new RemoteControlStatus(RemoteControlState.NotConnected, Detail: "Claude Code is closing."), RemoteControlProtocol.AfterWorkerShuttingDown(connected, message));
        Assert.Null(RemoteControlProtocol.AfterWorkerShuttingDown(RemoteControlStatus.NotConnected, message));
        Assert.Equal("Claude Code closed the connection (heartbeat lost).", RemoteControlProtocol.ShutdownReason("heartbeat_lost"));
    }

    [Fact]
    public void A_policy_turning_it_off_makes_it_not_available_unless_the_tab_was_leaving()
    {
        var connected = new RemoteControlStatus(RemoteControlState.Connected, SessionUrl);
        var message = new JsonObject { ["type"] = "system", ["subtype"] = "worker_shutting_down", ["reason"] = "remote_control_disabled" };

        Assert.Equal(new RemoteControlStatus(RemoteControlState.Unavailable, Detail: RemoteControlProtocol.TurnedOffByPolicy), RemoteControlProtocol.AfterWorkerShuttingDown(connected, message));
        Assert.True(RemoteControlProtocol.SaysTurnedOffByPolicy("worker_shutting_down", message));
        Assert.Equal(RemoteControlState.NotConnected, RemoteControlProtocol.AfterWorkerShuttingDown(connected, message, leaving: true)!.State);
        Assert.False(RemoteControlProtocol.SaysTurnedOffByPolicy("worker_shutting_down", message, leaving: true));
        Assert.True(RemoteControlProtocol.SaysTurnedOffByPolicy("bridge_state", Bridge("policy_disabled")));
        Assert.False(RemoteControlProtocol.SaysTurnedOffByPolicy("bridge_state", Bridge("failed")));
    }

    [Theory]
    [InlineData(null, null, null, null, true)]
    [InlineData(true, "claude.ai", "firstParty", null, true)]
    [InlineData(true, "claude.ai", null, "https://api.anthropic.com/", true)]
    [InlineData(false, "none", null, null, false)]
    [InlineData(true, "api_key", null, null, false)]
    [InlineData(true, "api_key_helper", null, null, false)]
    [InlineData(true, "third_party", null, null, false)]
    [InlineData(true, "claude.ai", "vertex", null, false)]
    [InlineData(true, "claude.ai", null, "http://127.0.0.1:8787", false)]
    public void Eligibility_follows_the_sign_in_and_the_endpoint(bool? loggedIn, string? method, string? provider, string? baseUrl, bool eligible)
    {
        var account = loggedIn is { } signedIn ? new AuthStatus(signedIn, method, provider, null, null, null, null, null) : null;

        var reason = RemoteControlEligibility.Check(account, name => name == RemoteControlEligibility.BaseUrlVariable ? baseUrl : null);

        Assert.Equal(eligible, reason is null);
    }

    [Fact]
    public void The_presence_file_is_there_only_while_present_and_a_stale_one_is_removed()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("data", "presence");
        Directory.CreateDirectory(folder.Combine("data"));
        File.WriteAllText(path, "");

        var presence = new PresenceFile(path);
        Assert.False(File.Exists(path));

        presence.SetPresent(true);
        Assert.True(File.Exists(path));
        Assert.True(presence.IsPresent);

        presence.SetPresent(false);
        Assert.False(File.Exists(path));

        presence.SetPresent(true);
        presence.Dispose();
        Assert.False(File.Exists(path));
        Assert.Equal("CLAUDE_CLIENT_PRESENCE_FILE", PresenceFile.Variable);
    }

    private static JsonObject Bridge(string state, string? detail = null) =>
        new() { ["type"] = "system", ["subtype"] = "bridge_state", ["state"] = state, ["detail"] = detail };
}
