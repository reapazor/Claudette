using System.ComponentModel;
using Claudette.Core.Perforce;
using Claudette.Core.Tests.Support;
using Claudette.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Perforce;

/// <summary>Running <c>p4</c>, and keeping a tab's ticket fresh (DESIGN.md §18, "Keeping the ticket fresh").</summary>
public sealed class PerforceTicketKeeperTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly FakeP4 _p4;
    private readonly PerforceClient _client;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"claudette-p4-{Guid.NewGuid():N}");
    private readonly PerforceLoginGate _gate = new();
    private readonly List<PerforceTicketKeeper> _keepers = [];

    public PerforceTicketKeeperTests()
    {
        Directory.CreateDirectory(_folder);
        _p4 = new FakeP4(_time) { Root = _folder };
        _client = new PerforceClient(_p4, _time);
    }

    public void Dispose()
    {
        foreach (var keeper in _keepers)
        {
            keeper.Dispose();
        }
        Directory.Delete(_folder, recursive: true);
    }

    private PerforceTarget Target => new(_folder);

    // ---- The p4 runner ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Detection_runs_p4_info_in_the_tabs_folder()
    {
        var workspace = await _client.DetectAsync(Target, TestContext.Current.CancellationToken);

        Assert.NotNull(workspace);
        Assert.Equal("ssl:perforce:1666", workspace.Port);
        Assert.Equal("matt-ws", workspace.Client);
        var info = Assert.Single(_p4.Runs, r => r.Command == "info");
        Assert.Equal(["-ztag", "info"], info.Spec.Arguments);
        Assert.Equal(_folder, info.Spec.WorkingDirectory);
    }

    [Fact]
    public async Task Detection_uses_the_per_folder_overrides()
    {
        var workspace = await _client.DetectAsync(Target with { Port = "other:1666", User = "build" }, TestContext.Current.CancellationToken);

        Assert.Equal("other:1666", workspace!.Port);
        Assert.Equal("build", workspace.User);
        Assert.Equal(["-p", "other:1666", "-u", "build", "-ztag", "info"], _p4.Runs.Single(r => r.Command == "info").Spec.Arguments);
        Assert.DoesNotContain(_p4.Runs, r => r.Command == "set" && r.Arguments.Contains("P4PORT"));
    }

    [Fact]
    public async Task No_workspace_no_server_or_no_p4_is_not_a_workspace()
    {
        _p4.IsWorkspace = false;
        Assert.Null(await _client.DetectAsync(Target, TestContext.Current.CancellationToken));

        _p4.IsWorkspace = true;
        _p4.Unreachable = true;
        Assert.Null(await _client.DetectAsync(Target, TestContext.Current.CancellationToken));

        var missing = new PerforceClient(new FakeProcessLauncher { StartFailure = new Win32Exception(2, "No such file") }, _time);
        Assert.Null(await missing.DetectAsync(Target, TestContext.Current.CancellationToken));
        Assert.Equal(TicketState.Unknown, (await missing.GetTicketStatusAsync(Target, Workspace(), TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task The_password_goes_to_standard_input_never_the_command_line()
    {
        var result = await _client.LoginAsync(Target, Workspace(), "s3cret", allHosts: false, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("User matt logged in.", result.Message);
        var login = Assert.Single(_p4.Logins);
        Assert.Equal(["-p", "ssl:perforce:1666", "-u", "matt", "login"], login.Spec.Arguments);
        Assert.Equal(["s3cret"], login.Input);
        Assert.DoesNotContain(_p4.Runs.SelectMany(r => r.Spec.Arguments), a => a.Contains("s3cret", StringComparison.Ordinal));
        Assert.Null(login.Spec.Environment);
    }

    [Fact]
    public async Task All_hosts_tickets_add_login_a()
    {
        await _client.LoginAsync(Target, Workspace(), "s3cret", allHosts: true, TestContext.Current.CancellationToken);

        Assert.Equal(["-p", "ssl:perforce:1666", "-u", "matt", "login", "-a"], _p4.Logins.Single().Spec.Arguments);
    }

    [Fact]
    public async Task Without_a_P4PORT_login_leaves_out_the_server()
    {
        _p4.Port = null;
        var workspace = await _client.DetectAsync(Target, TestContext.Current.CancellationToken);

        await _client.LoginAsync(Target, workspace!, "s3cret", allHosts: false, TestContext.Current.CancellationToken);

        Assert.Equal(["-u", "matt", "login"], _p4.Logins.Single().Spec.Arguments);
    }

    [Fact]
    public async Task A_wrong_password_is_reported()
    {
        var result = await _client.LoginAsync(Target, Workspace(), "wrong", allHosts: false, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.NeedsUserLogin);
        Assert.Equal("Password invalid.", result.Message);
    }

    // ---- The keeper -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_ticket_with_time_left_needs_no_login()
    {
        _p4.TicketExpires = _time.GetUtcNow().AddHours(11);
        var (keeper, passwords) = Keeper();

        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken));

        Assert.Empty(_p4.Logins);
        Assert.Equal(0, passwords.Asked);
        Assert.Equal(_time.GetUtcNow().AddHours(11), keeper.ExpiresAt);
    }

    [Fact]
    public async Task An_expired_ticket_is_renewed()
    {
        _p4.TicketExpires = _time.GetUtcNow().AddMinutes(-1);
        var (keeper, passwords) = Keeper();
        var events = Record(keeper);

        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken));

        Assert.Single(_p4.Logins);
        Assert.Equal(1, passwords.Asked);
        Assert.Contains(events, e => e.Kind == PerforceKeeperEventKind.LoggedIn);
        Assert.Equal(TicketState.Valid, keeper.Status?.State);
    }

    [Fact]
    public async Task A_ticket_about_to_run_out_is_renewed()
    {
        _p4.TicketExpires = _time.GetUtcNow().AddMinutes(20);
        var (keeper, _) = Keeper();

        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.TurnStart, TestContext.Current.CancellationToken));

        Assert.Single(_p4.Logins);
        Assert.Equal(_time.GetUtcNow() + _p4.TicketLifetime, _p4.TicketExpires);
    }

    [Fact]
    public async Task The_renew_before_time_comes_from_the_options()
    {
        _p4.TicketExpires = _time.GetUtcNow().AddMinutes(20);
        var (keeper, _) = Keeper(renewBefore: TimeSpan.FromMinutes(10));

        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.TurnStart, TestContext.Current.CancellationToken));

        Assert.Empty(_p4.Logins);
    }

    [Fact]
    public async Task It_checks_every_15_minutes_and_renews_in_time()
    {
        _p4.TicketExpires = _time.GetUtcNow().AddMinutes(50);
        var (keeper, _) = Keeper();
        keeper.Start();
        await keeper.CurrentCheck;
        Assert.Equal(1, _p4.StatusChecks);
        Assert.NotNull(keeper.Status);
        Assert.Empty(_p4.Logins);

        // Each tick's check must finish before the next tick, or the next one shares it instead of checking again.
        _time.Advance(PerforceTicketKeeper.CheckInterval);
        await keeper.CurrentCheck;
        Assert.Equal(2, _p4.StatusChecks);
        // 35 minutes left: still more than 30.
        Assert.Empty(_p4.Logins);

        _time.Advance(PerforceTicketKeeper.CheckInterval);
        await keeper.CurrentCheck;
        // 20 minutes left: renew.
        Assert.Single(_p4.Logins);
        Assert.Equal(_p4.TicketExpires, keeper.ExpiresAt);
    }

    [Fact]
    public async Task Callers_arriving_during_a_login_share_it()
    {
        var (keeper, passwords) = Keeper();
        _p4.HoldLogins = new TaskCompletionSource();

        var first = keeper.EnsureFreshAsync(PerforceCheckReason.Hook, TestContext.Current.CancellationToken);
        await WaitFor(() => _p4.Logins.Count == 1);
        var second = keeper.EnsureFreshAsync(PerforceCheckReason.Recovery, TestContext.Current.CancellationToken);
        var third = keeper.EnsureFreshAsync(PerforceCheckReason.TurnStart, TestContext.Current.CancellationToken);
        _p4.HoldLogins.SetResult();

        var results = await Task.WhenAll(first, second, third);
        Assert.Equal([true, true, true], results);
        Assert.Single(_p4.Logins);
        Assert.Equal(1, passwords.Asked);
    }

    [Fact]
    public async Task Tabs_on_the_same_server_and_user_log_in_once()
    {
        var (one, onePasswords) = Keeper();
        var (two, twoPasswords) = Keeper();
        _p4.HoldLogins = new TaskCompletionSource();

        var first = one.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken);
        await WaitFor(() => _p4.Logins.Count == 1);
        var second = two.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken);
        _p4.HoldLogins.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal([true, true], results);
        Assert.Single(_p4.Logins);
        Assert.Equal(1, onePasswords.Asked + twoPasswords.Asked);
    }

    [Fact]
    public async Task A_hook_trusts_a_recent_check()
    {
        _p4.TicketExpires = _time.GetUtcNow().AddHours(11);
        var (keeper, _) = Keeper();
        await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken);

        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.Hook, TestContext.Current.CancellationToken));
        Assert.Equal(1, _p4.StatusChecks);

        _time.Advance(PerforceTicketKeeper.RecentCheck);
        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.Hook, TestContext.Current.CancellationToken));
        Assert.Equal(2, _p4.StatusChecks);
    }

    [Fact]
    public async Task A_refused_password_is_asked_for_again_with_the_reason()
    {
        var (keeper, passwords) = Keeper(new FakePasswords("wrong", "still wrong", "s3cret"));

        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken));

        Assert.Equal(3, _p4.Logins.Count);
        Assert.Equal([null, "Password invalid.", "Password invalid."], passwords.Requests.Select(r => r.PreviousError));
        Assert.Equal([1, 2, 3], passwords.Requests.Select(r => r.Attempt));
        Assert.Equal(["s3cret"], passwords.Succeeded);
    }

    [Fact]
    public async Task Giving_up_after_a_refusal_is_a_failed_login()
    {
        var (keeper, _) = Keeper(new FakePasswords("wrong", null));
        var events = Record(keeper);

        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken));

        Assert.Contains(events, e => e is { Kind: PerforceKeeperEventKind.LoginFailed, Message: "Password invalid." });
        Assert.Equal(PerforceKeeperEventKind.LoginFailed, keeper.Problem?.Kind);
    }

    [Fact]
    public async Task No_password_waits_before_trying_again_unless_a_turn_starts()
    {
        var (keeper, passwords) = Keeper(new FakePasswords(null, null, "s3cret"));
        var events = Record(keeper);

        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken));
        Assert.Contains(events, e => e.Kind == PerforceKeeperEventKind.NoPassword);

        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.Hook, TestContext.Current.CancellationToken));
        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.Recovery, TestContext.Current.CancellationToken));
        Assert.Equal(1, passwords.Asked);

        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.TurnStart, TestContext.Current.CancellationToken));
        Assert.Equal(2, passwords.Asked);

        _time.Advance(PerforceTicketKeeper.RetryAfterFailure);
        Assert.True(await keeper.EnsureFreshAsync(PerforceCheckReason.Hook, TestContext.Current.CancellationToken));
        Assert.Equal(3, passwords.Asked);
    }

    [Fact]
    public async Task Single_sign_on_asks_the_user_to_log_in_and_checks_every_minute()
    {
        _p4.SingleSignOn = true;
        var workspace = await _client.DetectAsync(Target, TestContext.Current.CancellationToken);
        var (keeper, passwords) = Keeper(workspace: workspace);
        var events = Record(keeper);

        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken));
        Assert.Single(events, e => e.Kind == PerforceKeeperEventKind.NeedsUserLogin);
        Assert.Empty(_p4.Logins);
        Assert.Equal(0, passwords.Asked);
        var checks = _p4.StatusChecks;

        _time.Advance(PerforceTicketKeeper.UserLoginCheckInterval);
        await WaitFor(() => _p4.StatusChecks > checks);
        _p4.LogInYourself();
        _time.Advance(PerforceTicketKeeper.UserLoginCheckInterval);
        await WaitFor(() => keeper.Status?.State == TicketState.Valid);

        Assert.Null(keeper.Problem);
        Assert.Single(events, e => e.Kind == PerforceKeeperEventKind.NeedsUserLogin);
        Assert.Empty(_p4.Logins);
    }

    [Fact]
    public async Task An_unreachable_server_isnt_logged_in_to()
    {
        _p4.Unreachable = true;
        var (keeper, passwords) = Keeper();

        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.Start, TestContext.Current.CancellationToken));

        Assert.Equal(TicketState.Unreachable, keeper.Status?.State);
        Assert.Equal(0, passwords.Asked);
    }

    [Fact]
    public async Task A_disposed_keeper_stops_checking()
    {
        _p4.TicketExpires = _time.GetUtcNow().AddHours(11);
        var (keeper, _) = Keeper();
        keeper.Start();
        await WaitFor(() => _p4.StatusChecks == 1);

        keeper.Dispose();
        _time.Advance(PerforceTicketKeeper.CheckInterval * 3);

        Assert.Equal(1, _p4.StatusChecks);
        Assert.False(await keeper.EnsureFreshAsync(PerforceCheckReason.Hook, TestContext.Current.CancellationToken));
    }

    private PerforceWorkspace Workspace() => PerforceWorkspace.FromInfo($"... userName matt\n... clientName matt-ws\n... clientRoot {_folder}\n", _folder, "ssl:perforce:1666", null)!;

    private (PerforceTicketKeeper Keeper, FakePasswords Passwords) Keeper(FakePasswords? passwords = null, TimeSpan? renewBefore = null, PerforceWorkspace? workspace = null)
    {
        passwords ??= new FakePasswords("s3cret");
        var keeper = new PerforceTicketKeeper(_client, Target, workspace ?? Workspace(), passwords, _gate, _time,
            () => new PerforceKeeperOptions(renewBefore ?? TimeSpan.FromMinutes(30), AllHosts: false));
        _keepers.Add(keeper);
        return (keeper, passwords);
    }

    private static List<PerforceKeeperEvent> Record(PerforceTicketKeeper keeper)
    {
        var events = new List<PerforceKeeperEvent>();
        keeper.Changed += e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };
        return events;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Timed out waiting.");
    }

    /// <summary>Hands out the given passwords in turn; null is "none" (cancelled, or nothing stored).</summary>
    private sealed class FakePasswords(params string?[] passwords) : IPerforcePasswordProvider
    {
        private int _next;

        public List<PerforcePasswordRequest> Requests { get; } = [];

        public List<string> Succeeded { get; } = [];

        public int Asked => Requests.Count;

        public Task<string?> GetPasswordAsync(PerforcePasswordRequest request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
                return Task.FromResult(passwords[Math.Min(_next++, passwords.Length - 1)]);
            }
        }

        public Task OnLoginSucceededAsync(PerforcePasswordRequest request, string password, CancellationToken cancellationToken)
        {
            Succeeded.Add(password);
            return Task.CompletedTask;
        }
    }
}
