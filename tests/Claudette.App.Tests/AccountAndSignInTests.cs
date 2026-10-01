using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Auth;
using Claudette.Core.Claude;
using Claudette.Core.Installation;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.Tests;

/// <summary>
/// Sign-in in the middle of a session and the account (DESIGN.md §11): the banner, messages held until signed in,
/// tabs restarting after it, the sign-in dialog with its fallback and options, and signing out.
/// </summary>
public class AccountAndSignInTests
{
    // How Claude Code 2.1.284 answers a message when it isn't signed in (the signed-out protocol fixture).
    private const string SignedOutReply = """{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Not logged in · Please run /login"}]},"error":"authentication_failed"}""";
    private const string SignedOutResult = """{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login","session_id":"s1"}""";
    private const string Init = """{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""";

    private static readonly AuthStatus SignedIn = new(true, "claude.ai", "firstParty", "me@example.com", "Acme", "max", null, null);

    // ---- The banner ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tab_that_finds_Claude_Code_signed_out_shows_a_banner_and_keeps_the_tabs()
    {
        await using var h = new TabTestHarness();
        var account = Track(h);
        var tab = await h.OpenTabAsync();

        await SendAsync(tab, "hello");
        h.Transport.Emit(Init);
        h.Transport.Emit(SignedOutReply);
        h.Transport.Emit(SignedOutResult);
        await TabTestHarness.Eventually(() => account.NeedsSignIn, "the banner");

        Assert.True(h.Shell.NeedsSignIn);
        Assert.True(tab.IsWaitingForSignIn);
        Assert.Same(tab, h.Shell.SelectedTab);
        var notification = h.Notifier.Last("SignIn")!;
        Assert.Equal("Claude Code needs you to sign in", notification.Title);
    }

    [Fact]
    public async Task No_notification_while_Claudette_is_in_front_and_only_one_for_several_tabs()
    {
        await using var h = new TabTestHarness();
        var account = new AccountViewModel(h.Services);
        h.Services.Notifications.SetAppActive(true);

        account.RequireSignIn();
        Assert.True(account.NeedsSignIn);
        Assert.Empty(h.Notifier.Shown);

        h.Services.Notifications.SetAppActive(false);
        account.RequireSignIn();
        Assert.Empty(h.Notifier.Shown);
    }

    [Fact]
    public async Task Signing_in_takes_the_banner_and_the_notification_away()
    {
        await using var h = new TabTestHarness();
        var account = new AccountViewModel(h.Services);
        account.RequireSignIn();
        account.ShowSignIn();

        account.OnSignedIn(SignedIn);

        Assert.False(account.NeedsSignIn);
        Assert.False(account.HasSignIn);
        Assert.Contains("SignIn", h.Notifier.Removed);
        Assert.Equal("me@example.com · Max plan", account.Summary);
    }

    [Fact]
    public async Task Clicking_the_notification_opens_the_sign_in_dialog_ready_to_start()
    {
        await using var h = new TabTestHarness();
        var main = new MainWindowViewModel(h.Services);
        main.Account.RequireSignIn();

        main.OnNotificationActivated(new NotificationTarget(NotificationKind.SignIn, null));

        Assert.True(main.Account.HasSignIn);
        Assert.True(main.Account.SignIn!.IsIdle);
        Assert.True(main.Account.SignIn.CanClose);
    }

    // ---- Holding messages and restarting ---------------------------------------------------------------------------

    [Fact]
    public async Task Messages_wait_for_the_sign_in_and_the_session_resumes_after_it()
    {
        await using var h = new TabTestHarness();
        Track(h);
        var tab = await h.OpenTabAsync();
        await SendAsync(tab, "hello");
        h.Transport.Emit(Init);
        h.Transport.Emit(SignedOutReply);
        h.Transport.Emit(SignedOutResult);
        await TabTestHarness.Eventually(() => tab.IsWaitingForSignIn, "the sign-in error");

        await SendAsync(tab, "and this");

        // The first never reached the model, so it goes again; the second waits with it.
        Assert.Equal(["hello", "and this"], InlineDispatcher.Read(() => tab.HeldMessages.ToArray()));
        Assert.Equal(["hello"], h.Transport.SentUserTexts);
        Assert.Contains(InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().ToArray()), n => n.Text.Contains("Your messages are kept", StringComparison.Ordinal));

        await h.Shell.OnSignedInAgainAsync();
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Count() == 3, "the held messages");

        Assert.Equal(["hello", "hello", "and this"], h.Transport.SentUserTexts);
        Assert.Equal(2, h.Factory.Launches.Count);
        Assert.Equal("s1", h.Factory.Launches[1].Resume);
        Assert.False(h.Shell.NeedsSignIn);
        Assert.False(tab.IsWaitingForSignIn);
        Assert.Empty(tab.HeldMessages);
    }

    [Fact]
    public async Task Held_messages_keep_their_images()
    {
        await using var h = new TabTestHarness();
        Track(h);
        var tab = await h.OpenTabAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        tab.AddImage(png, "Pasted image");
        await SendAsync(tab, "look at this");
        h.Transport.Emit(Init);
        h.Transport.Emit(SignedOutReply);
        h.Transport.Emit(SignedOutResult);
        await TabTestHarness.Eventually(() => tab.IsWaitingForSignIn, "the sign-in error");

        // An image on its own, sent while waiting.
        tab.AddImage(png, "Pasted image");
        await tab.SendCommand.ExecuteAsync(null);
        Assert.Equal(["look at this", ""], InlineDispatcher.Read(() => tab.HeldMessages.ToArray()));
        Assert.Empty(tab.Attachments);

        await h.Shell.OnSignedInAgainAsync();
        await TabTestHarness.Eventually(() => UserMessages(h).Length == 3, "the held messages");

        var resent = UserMessages(h)[1..];
        Assert.Equal(["image", "text"], resent[0].Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("look at this", resent[0][1]!["text"]!.GetValue<string>());
        Assert.Equal(["image"], resent[1].Select(b => b!["type"]!.GetValue<string>()));
        Assert.All(resent, content => Assert.Equal(Convert.ToBase64String(png), content[0]!["source"]!["data"]!.GetValue<string>()));

        static JsonArray[] UserMessages(TabTestHarness h) =>
            [.. h.Transport.Sent.Where(m => m["type"]?.GetValue<string>() == "user").Select(m => m["message"]!["content"]!.AsArray())];
    }

    [Fact]
    public async Task A_held_slash_command_keeps_its_suffix_apart()
    {
        await using var h = new TabTestHarness();
        Track(h);
        var tab = await h.OpenTabAsync();
        await SendAsync(tab, "hello");
        h.Transport.Emit(Init);
        h.Transport.Emit(SignedOutReply);
        h.Transport.Emit(SignedOutResult);
        await TabTestHarness.Eventually(() => tab.IsWaitingForSignIn, "the sign-in error");

        tab.AddSuffixCommand.Execute(h.Services.Suffix("clarify"));
        await SendAsync(tab, "/review");
        await h.Shell.OnSignedInAgainAsync();
        await TabTestHarness.Eventually(() => h.Transport.Sent.Count(m => m["type"]?.GetValue<string>() == "user") == 3, "the held messages");

        // Still before the command, so it doesn't become the command's arguments (DESIGN.md §5, "Quick suffixes").
        var resent = h.Transport.Sent.Last(m => m["type"]?.GetValue<string>() == "user")["message"]!["content"]!.AsArray();
        Assert.Equal(["Ask clarifying questions before you start.", "/review"], resent.Select(b => b!["text"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_message_that_was_answered_is_not_sent_again()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        await SendAsync(tab, "first");
        h.Transport.EmitTurn("done");
        await TabTestHarness.Eventually(() => tab.State.Tokens.Turns == 1, "the turn");

        // A token that expires mid-session: the next message fails.
        await SendAsync(tab, "second");
        h.Transport.Emit(SignedOutReply);
        await TabTestHarness.Eventually(() => tab.IsWaitingForSignIn, "the sign-in error");

        Assert.Equal(["second"], InlineDispatcher.Read(() => tab.HeldMessages.ToArray()));
    }

    [Fact]
    public async Task A_tab_that_cant_start_signed_out_waits_and_starts_after_the_sign_in()
    {
        await using var h = new TabTestHarness();
        var account = Track(h);
        h.Factory.StartFailure = new ClaudeSessionExitedException(new TransportExit(1, "Invalid API key · Please run /login"));

        await h.Shell.OpenFolderAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.IsWaitingForSignIn, "the sign-in error");

        Assert.Equal(TabStatus.Error, tab.Status);
        Assert.Equal("Waiting for you to sign in", tab.StatusTip);
        Assert.True(account.NeedsSignIn);
        Assert.Null(h.Notifier.Last("ProcessError"));
        Assert.Contains(InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().ToArray()), n => n.Text.Contains("Invalid API key", StringComparison.Ordinal));

        // It can still be sent a message, which waits; selecting it again doesn't try to start it meanwhile.
        tab.ComposerText = "hello";
        Assert.True(tab.SendCommand.CanExecute(null));
        await tab.SendCommand.ExecuteAsync(null);
        Assert.Equal(["hello"], InlineDispatcher.Read(() => tab.HeldMessages.ToArray()));
        await tab.EnsureStartedAsync();
        Assert.Single(h.Factory.Launches);

        h.Factory.StartFailure = null;
        await h.Shell.OnSignedInAgainAsync();
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains("hello"), "the held message");

        Assert.Equal(2, h.Factory.Launches.Count);
        Assert.True(tab.IsProcessRunning);
        Assert.False(tab.IsWaitingForSignIn);
    }

    [Fact]
    public async Task Other_start_failures_are_still_errors()
    {
        await using var h = new TabTestHarness();
        h.Factory.StartFailure = new ClaudeSessionExitedException(new TransportExit(2, "Error: MCP server crashed"));

        await h.Shell.OpenFolderAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Error, "the error");

        Assert.False(tab.IsWaitingForSignIn);
        Assert.False(h.Shell.NeedsSignIn);
        Assert.NotNull(h.Notifier.Last("ProcessError"));
    }

    [Fact]
    public async Task A_start_Claude_Code_refused_says_why_and_offers_the_fix()
    {
        await using var h = new TabTestHarness();
        h.Factory.StartFailure = new ClaudeSessionExitedException(new TransportExit(1, "--dangerously-skip-permissions cannot be used with root/sudo privileges for security reasons"),
            new StartupFailure("bypass_root", "--dangerously-skip-permissions cannot be used with root/sudo privileges for security reasons"));

        await h.Shell.OpenFolderAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Error, "the error");

        Assert.Equal("Bypass permissions mode can't be used while running as root. Choose another permission mode in Tab settings…, then restart the tab.", tab.ErrorMessage);
        Assert.Equal("Claude Code couldn't start: Bypass permissions mode can't be used while running as root. Choose another permission mode in Tab settings…, then restart the tab.\nClaude Code said: --dangerously-skip-permissions cannot be used with root/sudo privileges for security reasons",
            InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
        Assert.False(tab.IsFolderMissing);
        Assert.NotNull(h.Notifier.Last("ProcessError"));

        // A folder Claude Code can't use is the missing folder's case: choose where it is now.
        h.Factory.StartFailure = new ClaudeSessionExitedException(new TransportExit(1, ""), new StartupFailure("cwd_unavailable", "The working directory is unavailable"));
        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await h.Shell.OpenFolderAsync(h.WorkFolder);
        var second = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => second.IsFolderMissing, "the folder");
        Assert.Equal(TabStatus.Error, second.Status);
    }

    // ---- The sign-in dialog: control requests ---------------------------------------------------------------------

    [Fact]
    public async Task Sign_in_opens_the_automatic_address_and_waits_for_Claude_Code()
    {
        await using var h = new TabTestHarness();
        AnswerAuthenticate(h);
        var signedIn = 0;
        var signIn = new SignInViewModel(h.Services, () => { signedIn++; return Task.CompletedTask; });

        var running = signIn.SignInCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Platform.OpenedUrls.Contains("https://claude.ai/automatic"), "the browser");

        Assert.True(signIn.IsWaiting);
        Assert.Equal("Finish signing in in your browser.", signIn.Status);
        Assert.True(Request(h, "claude_authenticate")["loginWithClaudeAi"]!.GetValue<bool>());

        Answer(h, "claude_oauth_wait_for_completion", new JsonObject { ["account"] = new JsonObject { ["email"] = "me@example.com" } });
        await running;

        Assert.Equal(1, signedIn);
        Assert.False(signIn.IsWaiting);
    }

    [Fact]
    public async Task A_code_from_the_browser_goes_through_claude_oauth_callback()
    {
        await using var h = new TabTestHarness();
        AnswerAuthenticate(h);
        var signIn = new SignInViewModel(h.Services, () => Task.CompletedTask);
        var running = signIn.SignInCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => signIn.Status == "Finish signing in in your browser.", "waiting");

        await signIn.EnterCodeInsteadCommand.ExecuteAsync(null);
        Assert.True(signIn.ShowCodeEntry);
        Assert.Contains("https://claude.ai/manual", h.Platform.OpenedUrls);

        h.Transport.Answers["claude_oauth_callback"] = _ => new JsonObject();
        signIn.Code = " abc123#state456 ";
        await signIn.SubmitCodeCommand.ExecuteAsync(null);

        var callback = Request(h, "claude_oauth_callback");
        Assert.Equal("abc123", callback["authorizationCode"]!.GetValue<string>());
        Assert.Equal("state456", callback["state"]!.GetValue<string>());

        Answer(h, "claude_oauth_wait_for_completion", new JsonObject());
        await running;
    }

    [Fact]
    public async Task A_failed_sign_in_shows_Claude_Codes_message_and_Try_again_repeats_it()
    {
        await using var h = new TabTestHarness();
        AnswerAuthenticate(h);
        h.Transport.Answers["claude_oauth_wait_for_completion"] = _ => throw new InvalidOperationException("Your organization isn't allowed to use Claude Code.");
        var signIn = new SignInViewModel(h.Services, () => Task.CompletedTask);

        await signIn.SignInWithConsoleCommand.ExecuteAsync(null);

        Assert.Equal("Your organization isn't allowed to use Claude Code.", signIn.Error);
        Assert.Equal("Try again", signIn.SignInText);
        Assert.True(signIn.IsIdle);
        Assert.False(Request(h, "claude_authenticate")["loginWithClaudeAi"]!.GetValue<bool>());

        // Try again: the same kind of account.
        await signIn.SignInCommand.ExecuteAsync(null);
        Assert.Equal(2, h.Transport.SentControlSubtypes.Count(s => s == "claude_authenticate"));
        Assert.False(Request(h, "claude_authenticate")["loginWithClaudeAi"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Sign_in_that_times_out_says_so()
    {
        await using var h = new TabTestHarness();
        AnswerAuthenticate(h);
        var signIn = new SignInViewModel(h.Services, () => Task.CompletedTask);
        var running = signIn.SignInCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Transport.SentControlSubtypes.Contains("claude_oauth_wait_for_completion"), "waiting");

        h.Time.Advance(UtilitySession.SignInTimeout);
        await running;

        Assert.Equal("Sign-in timed out. Try again.", signIn.Error);
        Assert.Equal("Try again", signIn.SignInText);
    }

    [Fact]
    public async Task I_have_already_signed_in_says_so_when_Claude_Code_disagrees()
    {
        await using var h = new TabTestHarness();
        SignInViewModel? signIn = null;
        signIn = new SignInViewModel(h.Services, () =>
        {
            signIn!.ShowStillSignedOut();
            return Task.CompletedTask;
        });

        await signIn.CheckAgainCommand.ExecuteAsync(null);

        Assert.Equal("Claude Code still reports that you're not signed in.", signIn.Error);
    }

    // ---- The sign-in dialog: claude auth login --------------------------------------------------------------------

    [Fact]
    public async Task Without_the_control_requests_it_runs_claude_auth_login_and_writes_the_code_to_it()
    {
        var launcher = new FakeLauncher();
        await using var h = WithAuth(launcher);
        h.Transport.Answers["claude_authenticate"] = _ => throw new InvalidOperationException("Unsupported control request subtype: claude_authenticate");
        launcher.OnStart = (spec, process) =>
        {
            if (spec.Arguments is ["auth", "login", ..])
            {
                process.WriteOutput("Opening browser to sign in…");
                process.WriteOutput("If the browser didn't open, visit: https://platform.claude.com/oauth/authorize?code=true&state=s");
            }
        };
        var signedIn = 0;
        var signIn = new SignInViewModel(h.Services, () => { signedIn++; return Task.CompletedTask; });

        var running = signIn.SignInWithConsoleCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => signIn.Status == "Finish signing in in your browser.", "the printed address");

        var login = launcher.Started.Single(s => s.Arguments is ["auth", "login", ..]);
        Assert.Equal(["auth", "login", "--console"], login.Arguments);
        Assert.DoesNotContain(login.Environment!.Keys, ClaudeEnvironment.SessionVariables.Contains);
        Assert.True(signIn.IsUsingCommand);
        // claude auth login opens the browser itself.
        Assert.Empty(h.Platform.OpenedUrls);

        // Its printed address is the page with a code, so opening it again asks for the code.
        await signIn.OpenBrowserAgainCommand.ExecuteAsync(null);
        Assert.Equal(["https://platform.claude.com/oauth/authorize?code=true&state=s"], h.Platform.OpenedUrls);
        Assert.True(signIn.ShowCodeEntry);

        signIn.Code = "abc123#s";
        await signIn.SubmitCodeCommand.ExecuteAsync(null);
        var process = launcher.Processes[launcher.Started.IndexOf(login)];
        Assert.Equal(["abc123#s"], process.Input);

        process.WriteOutput("Login successful.");
        process.Exit(0);
        await running;
        Assert.Equal(1, signedIn);
    }

    [Fact]
    public async Task SSO_signs_in_with_claude_auth_login()
    {
        var launcher = new FakeLauncher();
        await using var h = WithAuth(launcher);
        var signIn = new SignInViewModel(h.Services, () => Task.CompletedTask);

        var running = signIn.SignInWithSsoCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => launcher.Started.Count == 1, "claude auth login");

        Assert.Equal(["auth", "login", "--sso"], launcher.Started[0].Arguments);
        Assert.DoesNotContain("claude_authenticate", h.Transport.SentControlSubtypes);
        Assert.Equal("Signing in with single sign-on (SSO).", signIn.MethodText);

        launcher.Processes[0].WriteError("Login failed: SSO isn't set up for this organization");
        launcher.Processes[0].Exit(1);
        await running;

        Assert.Equal("Login failed: SSO isn't set up for this organization", signIn.Error);
        Assert.Equal("Try again", signIn.SignInText);
    }

    [Fact]
    public async Task Cancel_stops_claude_auth_login()
    {
        var launcher = new FakeLauncher();
        await using var h = WithAuth(launcher);
        var signIn = new SignInViewModel(h.Services, () => Task.CompletedTask);
        var running = signIn.SignInWithSsoCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => launcher.Started.Count == 1, "claude auth login");

        signIn.CancelCommand.Execute(null);
        await running;

        Assert.True(launcher.Processes[0].Killed);
        Assert.True(signIn.IsIdle);
        Assert.Null(signIn.Error);
        Assert.Equal(SignInViewModel.NeedsSignInText, signIn.Status);
    }

    // ---- The account and signing out -------------------------------------------------------------------------------

    [Theory]
    [InlineData("claude.ai", "me@example.com", "max", "me@example.com · Max plan")]
    [InlineData("claude.ai", "me@example.com", null, "me@example.com · Claude account")]
    [InlineData("api_key", null, null, "API key")]
    [InlineData("something_new", null, null, "something_new")]
    public async Task The_account_menu_shows_the_email_and_plan(string method, string? email, string? plan, string expected)
    {
        await using var h = new TabTestHarness();
        var account = new AccountViewModel(h.Services) { Status = new AuthStatus(true, method, "firstParty", email, null, plan, null, null) };

        Assert.Equal(expected, account.Summary);
    }

    [Theory]
    [InlineData("claude.ai", "firstParty", "max", AccountViewModel.ClaudeBillingUrl, "Plan and billing")]
    [InlineData("claude.ai", "firstParty", null, AccountViewModel.ClaudeBillingUrl, "Plan and billing")]
    [InlineData("oauth_token", "firstParty", null, AccountViewModel.ClaudeBillingUrl, "Plan and billing")]
    [InlineData("api_key", "firstParty", null, AccountViewModel.ConsoleBillingUrl, "Console billing")]
    [InlineData("third_party", "bedrock", null, null, null)]
    [InlineData("api_key", "vertex", null, null, null)]
    public async Task The_plan_links_to_where_its_billing_is_managed(string method, string provider, string? plan, string? url, string? label)
    {
        await using var h = new TabTestHarness();
        var account = new AccountViewModel(h.Services) { Status = new AuthStatus(true, method, provider, "me@example.com", null, plan, null, null) };

        Assert.Equal(url, account.BillingUrl);
        Assert.Equal(url is not null, account.HasBilling);
        if (label is not null)
        {
            Assert.Equal(label, account.BillingText);
            await account.OpenBillingCommand.ExecuteAsync(null);
            Assert.Equal([url!], h.Platform.OpenedUrls);
        }
    }

    [Fact]
    public async Task In_the_header_the_email_opens_the_menu_and_the_plan_beside_it_opens_its_usage()
    {
        await using var h = new TabTestHarness();
        var account = new AccountViewModel(h.Services) { Status = new AuthStatus(true, "claude.ai", "firstParty", "me@example.com", null, "max", null, null) };

        Assert.Equal("me@example.com", account.HeaderName);
        Assert.Equal("Max plan", account.HeaderPlan);
        Assert.Equal("Plan usage", account.HeaderPlanName);
        await account.OpenHeaderPlanCommand.ExecuteAsync(null);
        Assert.Equal(["https://claude.ai/new#settings/usage"], h.Platform.OpenedUrls);
        // The account menu's link still opens billing.
        Assert.Equal(AccountViewModel.ClaudeBillingUrl, account.BillingUrl);

        // A Console account has no plan usage on claude.ai: its plan goes to the Console's billing.
        account.Status = new AuthStatus(true, "api_key", "firstParty", "me@example.com", null, null, null, null);
        Assert.Equal("API key", account.HeaderPlan);
        Assert.Equal(AccountViewModel.ConsoleBillingUrl, account.HeaderPlanUrl);
        Assert.Equal("Console billing", account.HeaderPlanName);

        // With nowhere to manage billing, the header reads as before.
        account.Status = new AuthStatus(true, "third_party", "bedrock", null, null, null, null, null);
        Assert.Equal("Cloud provider", account.HeaderName);
        Assert.Null(account.HeaderPlan);

        account.Status = new AuthStatus(false, "none", "firstParty", null, null, null, null, null);
        Assert.Equal("Not signed in", account.HeaderName);
        Assert.Null(account.HeaderPlan);
    }

    [Fact]
    public async Task The_account_catches_up_with_claude_auth_status()
    {
        var launcher = new FakeLauncher
        {
            OnStart = (_, process) =>
            {
                process.WriteOutput("""{"loggedIn": false, "authMethod": "none"}""");
                process.Exit(1);
            },
        };
        await using var h = WithAuth(launcher);
        var account = new AccountViewModel(h.Services) { Status = SignedIn };

        await account.RefreshAsync();

        Assert.Equal(["auth", "status"], launcher.Started.Single().Arguments);
        Assert.Equal("Not signed in", account.Summary);
        Assert.True(account.CanBeginSignIn);
    }

    [Fact]
    public async Task Signed_out_the_account_menu_offers_sign_in()
    {
        await using var h = new TabTestHarness();
        var account = new AccountViewModel(h.Services) { Status = new AuthStatus(false, "none", "firstParty", null, null, null, null, null) };

        Assert.Equal("Not signed in", account.Summary);
        Assert.True(account.IsSignedOut);
        Assert.False(account.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task Sign_out_asks_first_then_runs_claude_auth_logout_and_the_tabs_wait_for_a_sign_in()
    {
        var launcher = new FakeLauncher();
        await using var h = WithAuth(launcher);
        var signedIn = true;
        launcher.OnStart = (spec, process) =>
        {
            switch (spec.Arguments)
            {
                case ["auth", "logout", ..]:
                    signedIn = false;
                    process.WriteOutput("Successfully logged out from your Anthropic account.");
                    process.Exit(0);
                    break;
                case ["auth", "status", ..]:
                    process.WriteOutput(signedIn ? """{"loggedIn": true, "authMethod": "claude.ai"}""" : """{"loggedIn": false, "authMethod": "none"}""");
                    process.Exit(signedIn ? 0 : 1);
                    break;
            }
        };
        var account = new AccountViewModel(h.Services) { Status = SignedIn };
        account.SignedOut += h.Shell.OnSignedOut;
        var tab = await h.OpenTabAsync();
        await SendAsync(tab, "hello");
        h.Transport.EmitTurn("hi");
        await TabTestHarness.Eventually(() => tab.State.Tokens.Turns == 1, "the turn");

        account.SignOutCommand.Execute(null);
        Assert.DoesNotContain(launcher.Started, s => s.Arguments is ["auth", "logout"]);
        Assert.Equal("Sign out of Claude Code?", account.SignOutConfirmation!.Title);
        Assert.Contains("Every tab stops working", account.SignOutConfirmation.Message, StringComparison.Ordinal);

        await account.SignOutConfirmation.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(launcher.Started, s => s.Arguments is ["auth", "logout"]);
        Assert.False(account.IsSignedIn);
        Assert.True(account.NeedsSignIn);
        Assert.True(h.Shell.NeedsSignIn);
        // The user did this, so no notification.
        Assert.Null(h.Notifier.Last("SignIn"));

        await SendAsync(tab, "after");
        Assert.Equal(["after"], InlineDispatcher.Read(() => tab.HeldMessages.ToArray()));

        // The next sign-in may be another account: the running tab picks its session up again.
        await h.Shell.OnSignedInAgainAsync();
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains("after"), "the held message");
        Assert.Equal(2, h.Factory.Launches.Count);
        Assert.Equal("s1", h.Factory.Launches[1].Resume);
    }

    [Fact]
    public async Task A_failed_sign_out_says_why_and_changes_nothing()
    {
        var launcher = new FakeLauncher
        {
            OnStart = (_, process) =>
            {
                process.WriteError("Logout failed: the keychain is locked");
                process.Exit(1);
            },
        };
        await using var h = WithAuth(launcher);
        var account = new AccountViewModel(h.Services) { Status = SignedIn };

        account.SignOutCommand.Execute(null);
        await account.SignOutConfirmation!.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't sign out: Logout failed: the keychain is locked", account.SignOutError);
        Assert.True(account.IsSignedIn);
        Assert.False(account.NeedsSignIn);
    }

    [Fact]
    public async Task Sign_out_says_so_when_an_API_key_keeps_Claude_Code_signed_in()
    {
        var launcher = new FakeLauncher
        {
            OnStart = (spec, process) =>
            {
                if (spec.Arguments is ["auth", "status", ..])
                {
                    process.WriteOutput("""{"loggedIn": true, "authMethod": "api_key"}""");
                }
                process.Exit(0);
            },
        };
        await using var h = WithAuth(launcher);
        var account = new AccountViewModel(h.Services) { Status = SignedIn };

        account.SignOutCommand.Execute(null);
        await account.SignOutConfirmation!.ConfirmCommand.ExecuteAsync(null);

        Assert.StartsWith("Claude Code is still signed in (API key).", account.SignOutError, StringComparison.Ordinal);
        Assert.False(account.NeedsSignIn);
    }

    [Fact]
    public async Task Settings_search_finds_sign_in_and_sign_out()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null) { Account = new AccountViewModel(h.Services) };

        settings.SearchText = "sign out";

        Assert.Contains(settings.SearchResults, r => r is { Category: "Claude Code", Label: "Sign out" });
        Assert.Equal("Claude Code", settings.SelectedCategory);
        Assert.True(settings.ClaudeCode.HasAccount);

        settings.SearchText = "sign in";
        Assert.Contains(settings.SearchResults, r => r is { Category: "Claude Code", Label: "Sign in" });
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>An account that the harness's shell reports sign-in errors to, as the main window's does.</summary>
    private static AccountViewModel Track(TabTestHarness h)
    {
        var account = new AccountViewModel(h.Services);
        h.OnAuthenticationRequired = () => account.RequireSignIn();
        return account;
    }

    /// <summary>A harness whose Claude Code runs <c>auth</c> commands through <paramref name="launcher"/>.</summary>
    private static TabTestHarness WithAuth(FakeLauncher launcher)
    {
        var h = new TabTestHarness(launcher: launcher);
        h.Services.UseInstall(new ClaudeInstall("claude", new Version(2, 1, 284)));
        h.Services.UseSessionFactory(h.Factory);
        return h;
    }

    private static async Task SendAsync(TabViewModel tab, string text)
    {
        tab.ComposerText = text;
        await tab.SendCommand.ExecuteAsync(null);
    }

    private static void AnswerAuthenticate(TabTestHarness h)
    {
        h.Transport.Answers["claude_authenticate"] = _ => new JsonObject { ["automaticUrl"] = "https://claude.ai/automatic", ["manualUrl"] = "https://claude.ai/manual" };
        // Answered later, once the test says the browser finished.
        h.Transport.Answers["claude_oauth_wait_for_completion"] = _ => null;
    }

    private static JsonObject Request(TabTestHarness h, string subtype) =>
        h.Transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == subtype)["request"]!.AsObject();

    private static void Answer(TabTestHarness h, string subtype, JsonObject response)
    {
        var id = h.Transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == subtype)["request_id"]!.GetValue<string>();
        h.Transport.Emit(OutgoingMessages.ControlSuccess(id, response));
    }
}
