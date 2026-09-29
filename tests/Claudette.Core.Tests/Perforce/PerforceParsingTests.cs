using Claudette.Core.Perforce;
using Claudette.Core.Processes;

namespace Claudette.Core.Tests.Perforce;

/// <summary>Reading <c>p4</c>'s output (DESIGN.md §18): <c>-ztag info</c>, <c>set</c> and <c>login -s</c>.</summary>
public class PerforceParsingTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "p4ws");

    private static string Info(string client = "matt-ws", string? root = null, string extra = "") =>
        $"""
        ... userName matt
        ... clientName {client}
        ... clientRoot {root ?? Root}
        ... clientCwd //{client}/src/...
        ... clientHost build-box
        ... serverAddress perforce:1666
        ... serverVersion P4D/LINUX26X86_64/2025.1/1234567 (2025/06/01)
        {extra}
        """;

    [Fact]
    public void Ztag_records_are_split_on_blank_lines_and_keep_multi_line_values()
    {
        var records = ZTag.Parse("... change 12\n... desc Fix the login\nand the logout\n\n... change 13\n... status pending\nnot a field\n");

        Assert.Equal(2, records.Count);
        Assert.Equal("Fix the login\nand the logout", records[0]["desc"]);
        Assert.Equal("pending\nnot a field", records[1]["status"]);
        Assert.Empty(ZTag.Parse("garbage\n\n"));
    }

    [Fact]
    public void Info_in_a_workspace_folder_is_a_workspace()
    {
        var workspace = PerforceWorkspace.FromInfo(Info(), Path.Combine(Root, "src"), "ssl:perforce:1666", null);

        Assert.NotNull(workspace);
        Assert.Equal("matt", workspace.User);
        Assert.Equal("matt-ws", workspace.Client);
        Assert.Equal("ssl:perforce:1666", workspace.Server);
        Assert.Equal("perforce:1666", workspace.ServerAddress);
        Assert.False(workspace.UsesSingleSignOn);
        Assert.StartsWith("P4D/", workspace.ServerVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_P4PORT_the_server_is_the_servers_own_address()
    {
        var workspace = PerforceWorkspace.FromInfo(Info(), Root, null, null)!;

        Assert.Null(workspace.Port);
        Assert.Equal("perforce:1666", workspace.Server);
    }

    [Fact]
    public void An_unknown_client_or_a_folder_outside_the_root_is_not_a_workspace()
    {
        Assert.Null(PerforceWorkspace.FromInfo(Info(client: "*unknown*"), Root, null, null));
        Assert.Null(PerforceWorkspace.FromInfo(Info(), Path.Combine(Path.GetTempPath(), "elsewhere"), null, null));
        Assert.Null(PerforceWorkspace.FromInfo(Info() + "\n", Path.Combine(Path.GetTempPath(), "p4ws-other"), null, null));
        Assert.Null(PerforceWorkspace.FromInfo("Perforce client error:\n\tConnect to server failed; check $P4PORT.", Root, null, null));
    }

    [Fact]
    public void A_workspace_with_no_root_counts_anywhere()
    {
        Assert.NotNull(PerforceWorkspace.FromInfo(Info(root: "null"), Path.Combine(Path.GetTempPath(), "elsewhere"), null, null));
    }

    [Fact]
    public void Single_sign_on_is_noticed()
    {
        Assert.True(PerforceWorkspace.FromInfo(Info(), Root, null, "/usr/local/bin/sso %user%")!.UsesSingleSignOn);
        Assert.True(PerforceWorkspace.FromInfo(Info(extra: "... ssoAuth required"), Root, null, null)!.UsesSingleSignOn);
    }

    [Fact]
    public void P4_set_values_are_read_with_or_without_their_source()
    {
        Assert.Equal("ssl:perforce:1666", PerforceWorkspace.ParseSetting("P4PORT=ssl:perforce:1666\n", "P4PORT"));
        Assert.Equal("ssl:perforce:1666", PerforceWorkspace.ParseSetting("P4PORT=ssl:perforce:1666 (config '/ws/.p4config')\r\n", "P4PORT"));
        Assert.Equal("pa ss(word)", PerforceWorkspace.ParseSetting("P4USER=matt\nP4PASSWD=pa ss(word)\n", "P4PASSWD"));
        Assert.Null(PerforceWorkspace.ParseSetting("", "P4PORT"));
        Assert.Null(PerforceWorkspace.ParseSetting("P4PORTX=1\n", "P4PORT"));
    }

    [Fact]
    public void The_workspace_note_names_the_server_user_and_workspace()
    {
        var note = PerforceWorkspace.FromInfo(Info(), Root, "ssl:perforce:1666", null)!.SystemPromptNote();

        Assert.Contains("Perforce workspace", note, StringComparison.Ordinal);
        Assert.Contains("ssl:perforce:1666", note, StringComparison.Ordinal);
        Assert.Contains("user matt", note, StringComparison.Ordinal);
        Assert.Contains("matt-ws", note, StringComparison.Ordinal);
        Assert.Contains("never run p4 login", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Tagged_login_status_gives_the_time_left()
    {
        var status = TicketStatus.Parse(new ProcessResult(0, "... User matt\n... Expiration 43140\n... TicketExpiration 43140\n", ""));

        Assert.Equal(TicketState.Valid, status.State);
        Assert.Equal(TimeSpan.FromSeconds(43140), status.ExpiresIn);
    }

    [Theory]
    [InlineData("User matt ticket expires in 11 hours 59 minutes.", 11 * 60 + 59)]
    [InlineData("User matt ticket expires in 1 hour 1 minute.", 61)]
    [InlineData("User matt ticket expires in 45 minutes.", 45)]
    [InlineData("User matt ticket expires in 2 hours.", 120)]
    public void Plain_login_status_gives_the_time_left(string output, int minutes)
    {
        var status = TicketStatus.Parse(new ProcessResult(0, output, ""));

        Assert.Equal(TicketState.Valid, status.State);
        Assert.Equal(TimeSpan.FromMinutes(minutes), status.ExpiresIn);
        Assert.Equal(output, status.Message);
    }

    [Theory]
    [InlineData(1, "", "Your session has expired, please login again.", TicketState.Expired)]
    [InlineData(1, "", "Your session was logged out, please login again.", TicketState.Expired)]
    [InlineData(1, "", "Perforce password (P4PASSWD) invalid or unset.", TicketState.NotLoggedIn)]
    [InlineData(0, "'login' not necessary, no password set for this user.", "", TicketState.NotNeeded)]
    [InlineData(1, "", "Perforce client error:\n\tConnect to server failed; check $P4PORT.\n\tTCP connect to perforce:1666 failed.", TicketState.Unreachable)]
    [InlineData(1, "", "Something new and strange.", TicketState.Unknown)]
    [InlineData(0, "User matt ticket does not expire.", "", TicketState.Valid)]
    public void Other_login_status_answers(int exitCode, string output, string error, TicketState expected)
    {
        var status = TicketStatus.Parse(new ProcessResult(exitCode, output, error));

        Assert.Equal(expected, status.State);
        Assert.Null(status.ExpiresIn);
        Assert.NotEmpty(status.Message);
    }

    [Fact]
    public void A_login_is_needed_when_expired_missing_or_about_to_run_out()
    {
        var renewBefore = TimeSpan.FromMinutes(30);

        Assert.True(new TicketStatus(TicketState.Expired, null, "").NeedsLogin(renewBefore));
        Assert.True(new TicketStatus(TicketState.NotLoggedIn, null, "").NeedsLogin(renewBefore));
        Assert.True(new TicketStatus(TicketState.Valid, TimeSpan.FromMinutes(29), "").NeedsLogin(renewBefore));
        Assert.False(new TicketStatus(TicketState.Valid, TimeSpan.FromMinutes(31), "").NeedsLogin(renewBefore));
        Assert.False(new TicketStatus(TicketState.Valid, null, "").NeedsLogin(renewBefore));
        Assert.False(new TicketStatus(TicketState.NotNeeded, null, "").NeedsLogin(renewBefore));
        Assert.False(new TicketStatus(TicketState.Unreachable, null, "").NeedsLogin(renewBefore));
    }

    [Theory]
    [InlineData(11 * 60 + 59, "11h")]
    [InlineData(3 * 60, "3h")]
    [InlineData(2 * 60 + 5, "2h 5m")]
    [InlineData(45, "45m")]
    [InlineData(0, "0m")]
    public void Durations_are_short(int minutes, string expected) =>
        Assert.Equal(expected, TicketStatus.Duration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Login_errors_are_recognized()
    {
        Assert.True(PerforceErrors.NeedsLogin("Your session has expired, please login again."));
        Assert.True(PerforceErrors.NeedsLogin("Exit code 1\nPerforce password (P4PASSWD) invalid or unset."));
        Assert.False(PerforceErrors.NeedsLogin("//depot/a.cpp#3 - opened for edit"));
        Assert.True(PerforceErrors.NeedsUserLogin("Navigate to URL: https://sso.example.com/login"));
        Assert.False(PerforceErrors.NeedsUserLogin("Password invalid."));
    }
}
