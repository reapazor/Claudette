using Claudette.Core.Auth;
using Claudette.Core.Claude;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Auth;

/// <summary>
/// Sign-in (DESIGN.md §11): the <c>claude auth login</c> fallback, <c>claude auth logout</c>, and telling sign-in
/// errors from the others.
/// </summary>
public class SignInTests
{
    private readonly FakeProcessLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new();

    // What Claude Code 2.1.284 prints with no terminal attached.
    private const string LoginOutput = """
        Opening browser to sign in…
        If the browser didn't open, visit: https://claude.ai/oauth/authorize?code=true&client_id=abc&state=xyz
        Paste code here if prompted >
        """;

    private ClaudeAuth Auth() => new("claude", _launcher, _time);

    // ---- claude auth login ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SignInMethod.ClaudeAi, new[] { "auth", "login" })]
    [InlineData(SignInMethod.Console, new[] { "auth", "login", "--console" })]
    [InlineData(SignInMethod.Sso, new[] { "auth", "login", "--sso" })]
    public void Each_kind_of_account_has_its_login_arguments(SignInMethod method, string[] expected)
    {
        Assert.Equal(expected, ClaudeLogin.Arguments(method));
    }

    [Fact]
    public void Login_runs_in_a_clean_environment()
    {
        _ = new ClaudeAuth("claude", _launcher, _time, new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = "/tmp/config" }).StartLogin(SignInMethod.Console);

        var spec = Assert.Single(_launcher.Started);
        Assert.Equal("claude", spec.FileName);
        Assert.Equal(["auth", "login", "--console"], spec.Arguments);
        Assert.Equal("/tmp/config", spec.Environment!["CLAUDE_CONFIG_DIR"]);
        Assert.DoesNotContain(spec.Environment.Keys, ClaudeEnvironment.SessionVariables.Contains);
    }

    [Theory]
    [InlineData("If the browser didn't open, visit: https://claude.ai/oauth/authorize?code=true&state=x", "https://claude.ai/oauth/authorize?code=true&state=x")]
    // An OSC 8 hyperlink with colour, as a terminal would get it.
    [InlineData("If the browser didn't open, visit: \u001b]8;;https://claude.ai/oauth/authorize?a=1\u0007\u001b[94mhttps://claude.ai/oauth/authorize?a=1\u001b[39m\u001b]8;;\u0007", "https://claude.ai/oauth/authorize?a=1")]
    [InlineData("Opening browser to sign in…", null)]
    public void Finds_the_printed_address(string line, string? expected)
    {
        Assert.Equal(expected, ClaudeLogin.FindUrl(line));
    }

    [Fact]
    public async Task Login_reports_the_address_takes_a_code_and_finishes()
    {
        var login = Auth().StartLogin(SignInMethod.ClaudeAi);
        var process = _launcher.Processes.Single();

        process.WriteOutput(LoginOutput);
        Assert.Equal("https://claude.ai/oauth/authorize?code=true&client_id=abc&state=xyz", await login.Url.WaitAsync(TestContext.Current.CancellationToken));

        await login.SubmitCodeAsync("  abc123#xyz \n", TestContext.Current.CancellationToken);
        Assert.Equal(["abc123#xyz"], process.Input);

        process.WriteOutput("Login successful.");
        process.Exit(0);
        await login.Completion.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_failed_login_carries_the_commands_message()
    {
        var login = Auth().StartLogin(SignInMethod.ClaudeAi);
        var process = _launcher.Processes.Single();

        process.WriteOutput(LoginOutput);
        process.WriteError("Login failed: Your organization isn't allowed to use Claude Code.");
        process.Exit(1);

        var failed = await Assert.ThrowsAsync<SignInFailedException>(() => login.Completion);
        Assert.Equal("Login failed: Your organization isn't allowed to use Claude Code.", failed.Message);
    }

    [Fact]
    public async Task A_login_that_says_nothing_still_fails_with_its_exit_code()
    {
        var login = Auth().StartLogin(SignInMethod.Sso);
        _launcher.Processes.Single().Exit(2);

        var failed = await Assert.ThrowsAsync<SignInFailedException>(() => login.Completion);
        Assert.Contains("exit code 2", failed.Message, StringComparison.Ordinal);
        Assert.Null(await login.Url);
    }

    [Fact]
    public async Task A_code_it_cannot_use_is_reported_while_it_keeps_waiting()
    {
        var login = Auth().StartLogin(SignInMethod.ClaudeAi);
        var errors = new List<string>();
        var seen = new TaskCompletionSource();
        login.ErrorLine += line =>
        {
            errors.Add(line);
            seen.TrySetResult();
        };

        _launcher.Processes.Single().WriteError("Invalid code. Please make sure the full code was copied.");
        await seen.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Invalid code. Please make sure the full code was copied."], errors);
        Assert.False(login.Completion.IsCompleted);
    }

    [Fact]
    public async Task Login_times_out_and_is_stopped()
    {
        var login = Auth().StartLogin(SignInMethod.ClaudeAi);

        _time.Advance(ClaudeAuth.LoginTimeout);

        var failed = await Assert.ThrowsAsync<SignInFailedException>(() => login.Completion);
        Assert.Equal("Sign-in timed out. Try again.", failed.Message);
        Assert.True(_launcher.Processes.Single().Killed);
    }

    [Fact]
    public async Task Cancelling_stops_the_login()
    {
        var login = Auth().StartLogin(SignInMethod.ClaudeAi);

        login.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.Completion);
        Assert.True(_launcher.Processes.Single().Killed);
    }

    // ---- claude auth logout --------------------------------------------------------------------------------------

    [Fact]
    public async Task Sign_out_runs_auth_logout()
    {
        _launcher.Respond = _ => new ProcessResult(0, "Successfully logged out from your Anthropic account.", "");

        await Auth().SignOutAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["auth", "logout"], _launcher.Started.Single().Arguments);
    }

    [Fact]
    public async Task A_failed_sign_out_carries_the_commands_message()
    {
        _launcher.Respond = _ => new ProcessResult(1, "", "Logout failed: the keychain is locked");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Auth().SignOutAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Logout failed: the keychain is locked", failed.Message);
    }

    // ---- Which errors a sign-in fixes ------------------------------------------------------------------------------

    [Theory]
    [InlineData("authentication_failed", true)]
    [InlineData("oauth_org_not_allowed", true)]
    [InlineData("rate_limit", false)]
    [InlineData("billing_error", false)]
    [InlineData("something_new", false)]
    [InlineData(null, false)]
    public void Knows_which_error_categories_need_a_sign_in(string? category, bool expected)
    {
        Assert.Equal(expected, SignInErrors.IsSignInCategory(category));
    }

    [Theory]
    [InlineData("Invalid API key · Please run /login", true)]
    [InlineData("Not logged in · Please run /login", true)]
    [InlineData("OAuth token revoked · Please run /login", true)]
    [InlineData("Error: ENOENT: no such file or directory", false)]
    [InlineData("", false)]
    public void Recognizes_a_session_that_couldnt_start_signed_out(string stderr, bool expected)
    {
        var exited = new ClaudeSessionExitedException(new TransportExit(1, stderr));

        Assert.Equal(expected, SignInErrors.IsSignInFailure(exited));
    }

    [Fact]
    public void Recognizes_a_sign_in_error_in_the_initialize_answer()
    {
        Assert.True(SignInErrors.IsSignInFailure(new ControlRequestException("initialize", "OAuth token has expired")));
        Assert.False(SignInErrors.IsSignInFailure(new ControlRequestException("initialize", "MCP server failed")));
        Assert.False(SignInErrors.IsSignInFailure(new TimeoutException("Claude Code didn't answer the 'initialize' control request.")));
    }
}
