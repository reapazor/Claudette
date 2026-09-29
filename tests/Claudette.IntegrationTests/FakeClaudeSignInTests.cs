using Claudette.Core.Auth;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.IntegrationTests.Support;

namespace Claudette.IntegrationTests;

/// <summary>
/// Sign-in against <c>fake-claude</c> (DESIGN.md §11, §15): <c>claude auth login</c> and <c>logout</c> as real
/// processes, the sign-in control requests, and sessions that find Claude Code signed out.
/// </summary>
public sealed class FakeClaudeSignInTests : IDisposable
{
    private readonly TempFolder _work = new();
    private readonly ClaudeSessionFactory _factory = new(FakeClaude.Path, new ProcessLauncher(), TimeProvider.System);

    public void Dispose() => _work.Dispose();

    /// <summary>A signed-out fake-claude, with its sign-in kept in a folder of this test's own.</summary>
    private Dictionary<string, string?> Environment(string login = "auto") => new()
    {
        ["CLAUDE_CONFIG_DIR"] = _work.Combine("config"),
        ["FAKE_CLAUDE_LOGGED_IN"] = "0",
        ["FAKE_CLAUDE_LOGIN"] = login,
        ["FAKE_CLAUDE_LOGIN_DELAY_MS"] = "50",
    };

    private ClaudeAuth Auth(Dictionary<string, string?> environment) => new(FakeClaude.Path, new ProcessLauncher(), TimeProvider.System, environment);

    [Fact]
    public async Task Auth_login_prints_its_address_and_signs_in()
    {
        var environment = Environment();
        var auth = Auth(environment);
        Assert.False((await auth.GetStatusAsync(TestContext.Current.CancellationToken)).LoggedIn);

        await using var login = auth.StartLogin(SignInMethod.ClaudeAi);

        Assert.StartsWith("https://fake-claude.invalid/oauth/authorize?code=true", await login.Url.WaitAsync(TestContext.Current.CancellationToken));
        await login.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var status = await auth.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.LoggedIn);
        Assert.Equal("max", status.SubscriptionType);
        Assert.Equal(_work.Combine("config", "projects"), status.ProjectsDirectory);
    }

    [Fact]
    public async Task Auth_login_takes_the_code_on_its_input()
    {
        var auth = Auth(Environment(login: "code"));
        await using var login = auth.StartLogin(SignInMethod.Sso);
        var invalid = new TaskCompletionSource<string>();
        login.ErrorLine += line => invalid.TrySetResult(line);
        Assert.Contains("method=sso", await login.Url.WaitAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        await login.SubmitCodeAsync("no-state-here", TestContext.Current.CancellationToken);
        Assert.Equal("Invalid code. Please make sure the full code was copied.", await invalid.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await login.SubmitCodeAsync("FAKE-CODE#fake-state", TestContext.Current.CancellationToken);

        await login.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True((await auth.GetStatusAsync(TestContext.Current.CancellationToken)).LoggedIn);
    }

    [Fact]
    public async Task A_failed_auth_login_reports_its_message()
    {
        var environment = Environment(login: "fail");
        environment["FAKE_CLAUDE_LOGIN_ERROR"] = "Your organization isn't allowed to use Claude Code";
        await using var login = Auth(environment).StartLogin(SignInMethod.Console);

        var failed = await Assert.ThrowsAsync<SignInFailedException>(() => login.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal("Login failed: Your organization isn't allowed to use Claude Code", failed.Message);
    }

    [Fact]
    public async Task Cancelling_auth_login_ends_it()
    {
        await using var login = Auth(Environment(login: "hang")).StartLogin(SignInMethod.ClaudeAi);
        await login.Url.WaitAsync(TestContext.Current.CancellationToken);

        login.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Auth_logout_signs_out()
    {
        var environment = Environment();
        environment["FAKE_CLAUDE_LOGGED_IN"] = "1";
        var auth = Auth(environment);
        Assert.True((await auth.GetStatusAsync(TestContext.Current.CancellationToken)).LoggedIn);

        await auth.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.False((await auth.GetStatusAsync(TestContext.Current.CancellationToken)).LoggedIn);
    }

    [Fact]
    public async Task A_failed_auth_logout_reports_its_message()
    {
        var environment = Environment();
        environment["FAKE_CLAUDE_LOGOUT_FAIL"] = "1";

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Auth(environment).SignOutAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Logout failed:", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_utility_session_signs_in_with_the_control_requests()
    {
        var environment = Environment();
        await using var utility = await UtilitySession.StartAsync(new WithEnvironment(_factory, environment), _work.Combine("utility"), TestContext.Current.CancellationToken);

        var urls = await utility.StartSignInAsync(claudeAiAccount: false, TestContext.Current.CancellationToken);
        Assert.Contains("method=console", urls.AutomaticUrl, StringComparison.Ordinal);
        Assert.Contains("code=true", urls.ManualUrl, StringComparison.Ordinal);

        var done = await utility.WaitForSignInAsync(TestContext.Current.CancellationToken);
        Assert.Equal("fake@example.com", done["account"]!["email"]!.GetValue<string>());
        var status = await Auth(environment).GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.LoggedIn);
        Assert.Null(status.SubscriptionType);
    }

    [Fact]
    public async Task The_utility_session_takes_a_code_from_the_browser()
    {
        var environment = Environment(login: "code");
        await using var utility = await UtilitySession.StartAsync(new WithEnvironment(_factory, environment), _work.Combine("utility"), TestContext.Current.CancellationToken);
        await utility.StartSignInAsync(cancellationToken: TestContext.Current.CancellationToken);
        var waiting = utility.WaitForSignInAsync(TestContext.Current.CancellationToken);

        await utility.SubmitSignInCodeAsync("FAKE-CODE#fake-state", TestContext.Current.CancellationToken);

        await waiting;
        Assert.True((await Auth(environment).GetStatusAsync(TestContext.Current.CancellationToken)).LoggedIn);
    }

    [Fact]
    public async Task A_Claude_Code_without_the_control_requests_rejects_them()
    {
        var environment = Environment();
        environment["FAKE_CLAUDE_AUTHENTICATE"] = "unsupported";
        await using var utility = await UtilitySession.StartAsync(new WithEnvironment(_factory, environment), _work.Combine("utility"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ControlRequestException>(() => utility.StartSignInAsync(cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ControlRequestException>(() => utility.WaitForSignInAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_session_that_cant_start_signed_out_is_recognized()
    {
        var environment = Environment();
        environment["FAKE_CLAUDE_START_AUTH_ERROR"] = "1";

        var failed = await Assert.ThrowsAnyAsync<Exception>(() => _factory.StartAsync(new ClaudeLaunchOptions { WorkingDirectory = _work.Path, EnvironmentOverrides = environment }, TestContext.Current.CancellationToken));

        Assert.True(SignInErrors.IsSignInFailure(failed), failed.ToString());
    }

    [Fact]
    public async Task A_signed_out_session_reports_that_it_needs_a_sign_in()
    {
        await using var session = await _factory.StartAsync(new ClaudeLaunchOptions { WorkingDirectory = _work.Path, EnvironmentOverrides = Environment() }, TestContext.Current.CancellationToken);

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (required, _) = await session.ReadUntilAsync<AuthenticationRequired>();
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("Not logged in · Please run /login", required.Detail);
        Assert.True(done.Result.IsError);
    }

    /// <summary>Starts sessions with extra environment variables, for the utility session, which sets none itself.</summary>
    private sealed class WithEnvironment(IClaudeSessionFactory inner, IReadOnlyDictionary<string, string?> environment) : IClaudeSessionFactory
    {
        public Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default) =>
            inner.StartAsync(options with { EnvironmentOverrides = environment }, cancellationToken);
    }
}
