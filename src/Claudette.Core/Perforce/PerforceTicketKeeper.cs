namespace Claudette.Core.Perforce;

/// <summary>Why the ticket is being checked, which decides how much work the check does.</summary>
public enum PerforceCheckReason
{
    /// <summary>The tab's session started, or the user asked: always a fresh check, and a login even right after a failed one.</summary>
    Start,
    /// <summary>A turn is starting: a fresh check, and a login even right after a failed one.</summary>
    TurnStart,
    /// <summary>Every 15 minutes.</summary>
    Scheduled,
    /// <summary>
    /// The PreToolUse hook, before a <c>p4</c> command. A recent check that found enough time left is trusted, so a turn
    /// full of <c>p4</c> commands doesn't run <c>p4 login -s</c> before each one.
    /// </summary>
    Hook,
    /// <summary>A <c>p4</c> command failed with an expired session.</summary>
    Recovery,
}

public enum PerforceKeeperEventKind
{
    /// <summary><c>p4 login -s</c> ran; <see cref="PerforceTicketKeeper.Status"/> is new.</summary>
    Checked,
    /// <summary>Claudette logged in.</summary>
    LoggedIn,
    /// <summary>There was no password to try: none stored or configured, or the prompt was cancelled.</summary>
    NoPassword,
    /// <summary>Perforce refused the password, or the login failed some other way.</summary>
    LoginFailed,
    /// <summary>Single sign-on or a second factor: the user has to log in themselves (DESIGN.md §18, "Not covered").</summary>
    NeedsUserLogin,
}

/// <summary>Something the tab shows or tells the user about.</summary>
public sealed record PerforceKeeperEvent(PerforceKeeperEventKind Kind, string? Message);

/// <summary>Settings → Perforce values the keeper reads each time, so changes apply to open tabs.</summary>
/// <param name="RenewBefore">Log in again when less than this is left on the ticket (default 30 minutes).</param>
/// <param name="AllHosts">Ask for tickets valid on every host (<c>p4 login -a</c>).</param>
public sealed record PerforceKeeperOptions(TimeSpan RenewBefore, bool AllHosts);

/// <summary>
/// Keeps one tab's Perforce login fresh (DESIGN.md §18, "Keeping the ticket fresh"): checks the ticket when the tab
/// starts, before each turn, before each <c>p4</c> command (through the PreToolUse hook) and every 15 minutes, and logs
/// in again when it has expired or is about to. Only one check or login runs at a time; callers arriving meanwhile
/// share it. Logins for the same server and user are serialized across tabs by <see cref="PerforceLoginGate"/>.
/// Thread-safe; <see cref="Changed"/> is raised on a background thread.
/// </summary>
public sealed class PerforceTicketKeeper : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>While waiting for the user to log in themselves (single sign-on), check this often.</summary>
    public static readonly TimeSpan UserLoginCheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>A hook trusts a check this recent, when it found more than the renew-before time left.</summary>
    public static readonly TimeSpan RecentCheck = TimeSpan.FromMinutes(5);

    /// <summary>After a login fails or is cancelled, hooks, recovery and the timer don't try again for this long.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    /// <summary>Passwords tried per login, when the provider has another one (a prompt after a refusal).</summary>
    public const int MaxAttempts = 3;

    private readonly PerforceClient _client;
    private readonly IPerforcePasswordProvider _passwords;
    private readonly PerforceLoginGate _gate;
    private readonly TimeProvider _time;
    private readonly Func<PerforceKeeperOptions> _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _sync = new();
    private Task<bool>? _running;
    private ITimer? _timer;
    private ITimer? _userLoginTimer;
    private DateTimeOffset? _failedAt;

    public PerforceTicketKeeper(
        PerforceClient client,
        PerforceTarget target,
        PerforceWorkspace workspace,
        IPerforcePasswordProvider passwords,
        PerforceLoginGate gate,
        TimeProvider timeProvider,
        Func<PerforceKeeperOptions> options)
    {
        _client = client;
        Target = target;
        Workspace = workspace;
        _passwords = passwords;
        _gate = gate;
        _time = timeProvider;
        _options = options;
    }

    public PerforceTarget Target { get; }

    public PerforceWorkspace Workspace { get; }

    /// <summary>The user Claudette logs in as: the per-folder override, else the workspace's user.</summary>
    public string User => Target.User ?? Workspace.User;

    /// <summary>The server Claudette logs in to, as shown.</summary>
    public string Server => Target.Port ?? Workspace.Server;

    /// <summary>The last <c>p4 login -s</c> result; null before the first check.</summary>
    public TicketStatus? Status { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>When the ticket runs out, as of the last check. Null when it isn't valid or Perforce didn't say.</summary>
    public DateTimeOffset? ExpiresAt => Status is { State: TicketState.Valid, ExpiresIn: { } left } && CheckedAt is { } at ? at + left : null;

    /// <summary>Why the last login didn't happen, until the ticket is valid again; for the info card.</summary>
    public PerforceKeeperEvent? Problem { get; private set; }

    public event Action<PerforceKeeperEvent>? Changed;

    /// <summary>The check under way (shared by everyone who asks meanwhile), or a completed task: for tests to wait on.</summary>
    internal Task CurrentCheck
    {
        get
        {
            lock (_sync)
            {
                return _running ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>Checks now, and every 15 minutes from now.</summary>
    public void Start()
    {
        _timer ??= _time.CreateTimer(_ => _ = EnsureFreshAsync(PerforceCheckReason.Scheduled), null, CheckInterval, CheckInterval);
        _ = EnsureFreshAsync(PerforceCheckReason.Start);
    }

    /// <summary>
    /// Makes sure the ticket is valid for at least the renew-before time, logging in if needed. Returns whether it is
    /// valid (or no login is needed) afterwards. Cancelling stops the wait, not a login already under way.
    /// </summary>
    public Task<bool> EnsureFreshAsync(PerforceCheckReason reason, CancellationToken cancellationToken = default)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }
        if (reason == PerforceCheckReason.Hook && IsRecentlyValid())
        {
            return Task.FromResult(true);
        }
        Task<bool> run;
        lock (_sync)
        {
            run = _running is { IsCompleted: false } running ? running : _running = Task.Run(() => RunAsync(reason));
        }
        return run.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _timer?.Dispose();
        _userLoginTimer?.Dispose();
    }

    /// <summary>A check or login is under way, which an <see cref="EnsureFreshAsync"/> call now would share.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_sync)
            {
                return _running is { IsCompleted: false };
            }
        }
    }

    private bool IsRecentlyValid()
    {
        var now = _time.GetUtcNow();
        return Status is { } status && CheckedAt is { } at && now - at < RecentCheck
            && (status.State == TicketState.NotNeeded
                || status.State == TicketState.Valid && (ExpiresAt is not { } expires || expires - now >= _options().RenewBefore));
    }

    private async Task<bool> RunAsync(PerforceCheckReason reason)
    {
        var token = _lifetime.Token;
        try
        {
            var status = await CheckAsync(token).ConfigureAwait(false);
            if (!status.NeedsLogin(_options().RenewBefore))
            {
                return IsUsable(status);
            }
            if (Workspace.UsesSingleSignOn)
            {
                AwaitUserLogin("This Perforce server uses single sign-on, so Claudette can't log in for you.");
                return false;
            }
            if (_userLoginTimer is not null)
            {
                // Still waiting for the user to log in themselves.
                return false;
            }
            if (reason is not (PerforceCheckReason.Start or PerforceCheckReason.TurnStart)
                && _failedAt is { } failed && _time.GetUtcNow() - failed < RetryAfterFailure)
            {
                return false;
            }
            return await _gate.RunAsync($"{Server}|{User}", () => LoginAsync(token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> LoginAsync(CancellationToken token)
    {
        // Another tab may have logged in while this one waited for the gate.
        var status = await CheckAsync(token).ConfigureAwait(false);
        if (!status.NeedsLogin(_options().RenewBefore))
        {
            return IsUsable(status);
        }
        string? error = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var request = new PerforcePasswordRequest(Workspace, Target, attempt, error);
            var password = await _passwords.GetPasswordAsync(request, token).ConfigureAwait(false);
            if (password is null)
            {
                Fail(error is null ? PerforceKeeperEventKind.NoPassword : PerforceKeeperEventKind.LoginFailed, error);
                return false;
            }
            var result = await _client.LoginAsync(Target, Workspace, password, _options().AllHosts, token).ConfigureAwait(false);
            if (result.Succeeded)
            {
                _failedAt = null;
                Problem = null;
                try
                {
                    await _passwords.OnLoginSucceededAsync(request, password, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Saving the password is a convenience; the login itself worked.
                }
                await CheckAsync(token).ConfigureAwait(false);
                Raise(new PerforceKeeperEvent(PerforceKeeperEventKind.LoggedIn, result.Message));
                return true;
            }
            if (result.NeedsUserLogin)
            {
                AwaitUserLogin(result.Message);
                return false;
            }
            error = result.Message;
        }
        Fail(PerforceKeeperEventKind.LoginFailed, error);
        return false;
    }

    private async Task<TicketStatus> CheckAsync(CancellationToken token)
    {
        var status = await _client.GetTicketStatusAsync(Target, Workspace, token).ConfigureAwait(false);
        Status = status;
        CheckedAt = _time.GetUtcNow();
        if (IsUsable(status) && !status.NeedsLogin(_options().RenewBefore))
        {
            Problem = null;
            _failedAt = null;
            _userLoginTimer?.Dispose();
            _userLoginTimer = null;
        }
        Raise(new PerforceKeeperEvent(PerforceKeeperEventKind.Checked, status.Message));
        return status;
    }

    private void Fail(PerforceKeeperEventKind kind, string? message)
    {
        _failedAt = _time.GetUtcNow();
        Raise(Problem = new PerforceKeeperEvent(kind, message));
    }

    /// <summary>Tells the user to log in themselves, then checks every minute until they have.</summary>
    private void AwaitUserLogin(string message)
    {
        var first = _userLoginTimer is null;
        _userLoginTimer ??= _time.CreateTimer(_ => _ = EnsureFreshAsync(PerforceCheckReason.Scheduled), null, UserLoginCheckInterval, UserLoginCheckInterval);
        Problem = new PerforceKeeperEvent(PerforceKeeperEventKind.NeedsUserLogin, message);
        if (first)
        {
            Raise(Problem);
        }
    }

    private static bool IsUsable(TicketStatus status) => status.State is TicketState.Valid or TicketState.NotNeeded;

    private void Raise(PerforceKeeperEvent e)
    {
        if (!_lifetime.IsCancellationRequested)
        {
            Changed?.Invoke(e);
        }
    }
}
