using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Perforce;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab's Perforce password prompt (DESIGN.md §18): for "Ask each time", and for "Stored by Claudette" when nothing is
/// saved yet or the saved password was refused. The password is handed over once and not kept.
/// </summary>
public sealed partial class PerforcePasswordPrompt(PerforcePasswordRequest request, bool offerToSave, string storeName) : ObservableObject
{
    private readonly TaskCompletionSource<PerforcePasswordAnswer?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Message => $"Perforce needs the password for {request.User} @ {request.Server} to keep this tab logged in.";

    /// <summary>Why the last password didn't work.</summary>
    public string? Error => request.PreviousError is { } error ? $"{error} Try again." : null;

    public bool HasError => Error is not null;

    public bool OfferToSave { get; } = offerToSave;

    public string SaveText => $"Save it in {storeName}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogInCommand))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool Save { get; set; } = true;

    /// <summary>Completes with the user's answer; null when cancelled.</summary>
    public Task<PerforcePasswordAnswer?> Answer => _answer.Task;

    [RelayCommand(CanExecute = nameof(CanLogIn))]
    private void LogIn()
    {
        _answer.TrySetResult(new PerforcePasswordAnswer(Password, OfferToSave && Save));
        Password = "";
    }

    private bool CanLogIn() => Password.Length > 0;

    [RelayCommand]
    private void Cancel() => Abandon();

    internal void Abandon()
    {
        _answer.TrySetResult(null);
        Password = "";
    }
}

/// <summary>A changelist in the tab info flyout, with Copy and Open in P4V (DESIGN.md §18).</summary>
public sealed record ChangelistRow(long Number, ChangelistState State)
{
    public string Text => State == ChangelistState.Submitted ? $"CL {Number} · submitted" : $"CL {Number}";
}

/// <summary>What the Perforce area needs from its tab (DESIGN.md §18).</summary>
internal interface IPerforceHost : ITabAreaHost
{
    /// <summary>A turn is running, so a retry can be sent mid-turn.</summary>
    bool IsWorking { get; }

    /// <summary>Sends a message as the tab, the way a queued message is sent.</summary>
    Task SendRawAsync(string text);

    /// <summary>Something waits on the user, like a permission prompt: "Needs input", the badge and check-ins follow.</summary>
    void WaitOnUser(object key);

    /// <summary>What <see cref="WaitOnUser"/> was given no longer waits.</summary>
    void Resolved(object key);

    /// <summary>The changelist changed: a project link can have <c>{changelist}</c> in it (DESIGN.md §18, "Links").</summary>
    void LinkValuesChanged();
}

/// <summary>
/// A tab's Perforce ticket handling and changelist (DESIGN.md §18): the ticket keeper for the session, the password
/// prompt, recovery after a <c>p4</c> command finds the login expired, and the changelist in the tab title.
/// </summary>
public sealed partial class PerforceViewModel : ObservableObject
{
    /// <summary>What Claudette sends mid-turn once it has logged in again after a <c>p4</c> command failed.</summary>
    public const string RetryMessage = "Perforce login renewed. Retry the last p4 command.";

    private readonly AppServices _services;
    private readonly IPerforceHost _host;

    private readonly BashCommandLog _bashCommands = new();
    private PerforceTicketKeeper? _perforceKeeper;
    private bool _perforceKeeperStarted;
    private ChangelistTracker? _changelistTracker;
    /// <summary>A <c>p4</c> command failed this turn and the login isn't back yet: ask Claude to retry once it is.</summary>
    private bool _perforceRetryPending;
    /// <summary>At most one retry message per turn, so a command failing for another reason can't loop.</summary>
    private bool _perforceRetrySent;
    private bool _perforceRecovering;

    internal PerforceViewModel(AppServices services, IPerforceHost host)
    {
        _services = services;
        _host = host;
    }

    private ChangelistTracker Changelists => _changelistTracker ??= new ChangelistTracker(_host.State.Changelists);

    /// <summary>The Perforce workspace this tab's folder is in, while ticket handling is on for it.</summary>
    public PerforceWorkspace? Workspace => _perforceKeeper?.Workspace;

    /// <summary>For tests: the ticket keeper is checking or logging in.</summary>
    internal bool IsCheckingTicket => _perforceKeeper?.IsBusy == true;

    /// <summary>The password prompt, while one is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt))]
    public partial PerforcePasswordPrompt? Prompt { get; set; }

    public bool HasPrompt => Prompt is not null;

    /// <summary>For example "matt @ ssl:perforce:1666, ticket expires in 11h", for the info card.</summary>
    public string? StatusText
    {
        get
        {
            if (_perforceKeeper is not { } keeper)
            {
                return null;
            }
            var state = keeper.Problem?.Kind == PerforceKeeperEventKind.NeedsUserLogin
                ? "log in yourself (single sign-on or a second factor)"
                : keeper.Status switch
                {
                    null => "checking the login…",
                    { State: TicketState.Valid } when keeper.ExpiresAt is { } expires => $"ticket expires in {TicketStatus.Duration(expires - _services.Time.GetUtcNow())}",
                    { State: TicketState.Valid } => "logged in",
                    { State: TicketState.NotNeeded } => "no login needed",
                    { State: TicketState.Expired } => "ticket expired",
                    { State: TicketState.NotLoggedIn } => "not logged in",
                    { State: TicketState.Unreachable } => "server unreachable",
                    { Message: var message } => message,
                };
            if (keeper.Problem is { Kind: PerforceKeeperEventKind.LoginFailed, Message: { } error })
            {
                state += $"; the last login failed: {error}";
            }
            return $"{keeper.User} @ {keeper.Server}, {state}";
        }
    }

    // ---- The changelist (DESIGN.md §18, "Perforce changelist in the tab title") ------------------------------------

    /// <summary>"CL 12345", or "CL 12345 · submitted"; null when there's no changelist to show.</summary>
    public string? ChangelistBadge => Changelists.Current is { } current ? new ChangelistRow(current.Number, current.State).Text : null;

    /// <summary>The badge after the tab's name, when Settings → Perforce → Show changelist on tabs is on.</summary>
    public bool ShowChangelistBadge => _services.Settings.Perforce.ShowChangelistOnTabs && ChangelistBadge is not null;

    public string ChangelistBadgeTip => $"Perforce changelist {Changelists.Current?.Number}. Click to copy it or open it in P4V.";

    /// <summary>The changelist the tab is on, for project links (DESIGN.md §18, "Links").</summary>
    public long? CurrentChangelist => Changelists.Current?.Number;

    /// <summary>Every changelist used in this session, most recent first.</summary>
    public IReadOnlyList<ChangelistRow> ChangelistRows => Changelists.All.Select(c => new ChangelistRow(c.Number, c.State)).ToArray();

    public bool HasChangelists => ChangelistRows.Count > 0;

    /// <summary>Copies a changelist number; null copies the tab's current one.</summary>
    [RelayCommand]
    private Task CopyChangelistAsync(long? number) =>
        (number ?? Changelists.Current?.Number) is { } n ? _services.Platform.SetClipboardTextAsync(n.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Task.CompletedTask;

    /// <summary><b>Open in P4V</b>: <c>p4v -cmd "open changelist 12345"</c>. Null opens the tab's current one.</summary>
    [RelayCommand]
    private void OpenChangelistInP4V(long? number)
    {
        if ((number ?? Changelists.Current?.Number) is not { } n)
        {
            return;
        }
        try
        {
            _services.Perforce.OpenInP4V(n, _host.Folder, Workspace, _perforceKeeper?.Target ?? _services.Perforce.TargetFor(_host.Folder));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _host.AddNote($"Couldn't open changelist {n} in P4V: {ex.Message}", NoteKind.Error);
        }
    }

    private void ChangelistsChanged()
    {
        OnPropertyChanged(nameof(ChangelistBadge));
        OnPropertyChanged(nameof(ShowChangelistBadge));
        OnPropertyChanged(nameof(ChangelistBadgeTip));
        OnPropertyChanged(nameof(ChangelistRows));
        OnPropertyChanged(nameof(HasChangelists));
        _host.InfoRowsChanged();
        _host.LinkValuesChanged();
    }

    /// <summary>The Perforce rows of the tab info card (DESIGN.md §4).</summary>
    internal void AddInfoRows(List<InfoRow> rows)
    {
        if (StatusText is { } status)
        {
            rows.Add(new InfoRow("Perforce", status));
        }
        var changelists = ChangelistRows;
        if (changelists.Count > 0)
        {
            var text = changelists[0].Text;
            if (changelists.Count > 1)
            {
                text += "; earlier: " + string.Join(", ", changelists.Skip(1).Select(c => c.Text));
            }
            rows.Add(new InfoRow("Changelist", text));
        }
    }

    /// <summary>Settings → Perforce changed: the badge may have been turned on or off.</summary>
    internal void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowChangelistBadge));
        _host.InfoRowsChanged();
    }

    // ---- Ticket handling (DESIGN.md §18, "Perforce ticket handling") --------------------------------------------------

    /// <summary>
    /// Before the tab's <c>claude</c> starts: when ticket handling is on and the folder is in a Perforce workspace, adds
    /// the workspace note (<c>--append-system-prompt</c>) and the PreToolUse hook for Bash, and gets a ticket keeper
    /// ready. It starts with the session.
    /// </summary>
    internal async Task<ClaudeLaunchOptions> WithPerforceAsync(ClaudeLaunchOptions options)
    {
        Stop();
        if (!_services.Settings.Perforce.Enabled)
        {
            return options;
        }
        var perforce = _services.Perforce;
        var target = perforce.TargetFor(_host.Folder);
        var workspace = await perforce.Client.DetectAsync(target);
        if (workspace is null)
        {
            return options;
        }
        var keeper = new PerforceTicketKeeper(perforce.Client, target, workspace, new PerforcePasswords(perforce, AskForPerforcePasswordAsync), perforce.Gate, _services.Time, perforce.KeeperOptions);
        keeper.Changed += e => _services.Dispatcher.Post(() => OnPerforceKeeperChanged(keeper, e));
        _perforceKeeper = keeper;
        perforce.AddKnownLogin(keeper.Server, keeper.User);
        OnPropertyChanged(nameof(Workspace));
        _host.InfoRowsChanged();
        var note = workspace.SystemPromptNote(target.User);
        return options with
        {
            AppendSystemPrompt = options.AppendSystemPrompt is { Length: > 0 } existing ? $"{existing}\n\n{note}" : note,
            Hooks = [.. options.Hooks, new HookRegistration("PreToolUse", "Bash", (input, token) => CheckBeforeP4Async(keeper, input, token), PerforceService.HookTimeout)],
        };
    }

    /// <summary>
    /// The PreToolUse hook: before a Bash command that runs <c>p4</c>, make sure the ticket is valid. It never changes
    /// the command. When a login is still waiting on the user near the hook's timeout, it lets the command run anyway,
    /// because a hook that times out stops the command without running it; recovery then takes over.
    /// </summary>
    private async Task<JsonObject> CheckBeforeP4Async(PerforceTicketKeeper keeper, HookInput input, CancellationToken cancellationToken)
    {
        if (!PerforceCommands.RunsP4(input.Command))
        {
            return HookOutputs.Continue();
        }
        using var deadline = new CancellationTokenSource(PerforceService.HookTimeout - PerforceService.HookMargin, _services.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await keeper.EnsureFreshAsync(PerforceCheckReason.Hook, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Out of time: let the command run.
        }
        return HookOutputs.Continue();
    }

    /// <summary>Each session event, on the UI thread.</summary>
    internal void OnSessionEvent(SessionEvent sessionEvent)
    {
        if (_perforceKeeper is { } keeper && !_perforceKeeperStarted)
        {
            // The first event of a new session: start checking now that there's a tab to show prompts in.
            _perforceKeeperStarted = true;
            keeper.Start();
        }
        switch (sessionEvent)
        {
            case StateChanged { State: SessionState.Working }:
                _perforceRetrySent = false;
                _ = _perforceKeeper?.EnsureFreshAsync(PerforceCheckReason.TurnStart);
                break;
            case AssistantMessageReceived assistant:
                _bashCommands.OnAssistantMessage(assistant.Message);
                break;
            case ToolResultsReceived results:
                foreach (var result in _bashCommands.OnToolResults(results.Message))
                {
                    OnBashResult(result);
                }
                break;
            case TurnCompleted:
                _perforceRetryPending = false;
                break;
            case SessionExited:
                _bashCommands.Clear();
                Stop();
                break;
        }
    }

    private void OnBashResult(BashResult result)
    {
        if (Changelists.Observe(result.Command, result.Output, result.IsError, _services.Time.GetUtcNow()))
        {
            ChangelistsChanged();
            _services.SaveState();
        }
        if (_perforceKeeper is not null && PerforceErrors.NeedsLogin(result.Output) && PerforceCommands.RunsP4(result.Command))
        {
            _ = RecoverPerforceLoginAsync();
        }
    }

    /// <summary>
    /// A <c>p4</c> command failed with an expired session (DESIGN.md §18, "Recovery"): log in again, then ask Claude to
    /// retry, mid-turn, the way a queued message is sent.
    /// </summary>
    private async Task RecoverPerforceLoginAsync()
    {
        if (_perforceKeeper is not { } keeper || _perforceRecovering || _perforceRetrySent)
        {
            return;
        }
        _perforceRecovering = true;
        try
        {
            if (await keeper.EnsureFreshAsync(PerforceCheckReason.Recovery))
            {
                await SendPerforceRetryAsync();
            }
            else if (!_perforceRetryPending && ReferenceEquals(keeper, _perforceKeeper))
            {
                _perforceRetryPending = true;
                _host.AddNote("A p4 command failed because the Perforce login expired. Claudette will ask Claude to retry once it's logged in again.", NoteKind.Warning);
            }
        }
        finally
        {
            _perforceRecovering = false;
        }
    }

    private async Task SendPerforceRetryAsync()
    {
        _perforceRetryPending = false;
        if (_perforceRetrySent || _host.Session is null || !_host.IsWorking)
        {
            return;
        }
        _perforceRetrySent = true;
        _host.AddNote("Logged in to Perforce again, and asked Claude to retry the last p4 command.");
        await _host.SendRawAsync(RetryMessage);
    }

    private void OnPerforceKeeperChanged(PerforceTicketKeeper keeper, PerforceKeeperEvent e)
    {
        if (!ReferenceEquals(keeper, _perforceKeeper))
        {
            return;
        }
        OnPropertyChanged(nameof(StatusText));
        _host.InfoRowsChanged();
        var who = $"{keeper.User} @ {keeper.Server}";
        switch (e.Kind)
        {
            case PerforceKeeperEventKind.Checked or PerforceKeeperEventKind.LoggedIn:
                if (_perforceRetryPending && keeper.Status is { State: TicketState.Valid or TicketState.NotNeeded })
                {
                    _ = SendPerforceRetryAsync();
                }
                break;
            case PerforceKeeperEventKind.NoPassword:
                _host.AddNote(_services.Settings.Perforce.PasswordSource == PerforcePasswordSource.PerforceConfig
                    ? $"Couldn't log in to Perforce as {who}: P4PASSWD isn't set for this folder. Set it, or choose another password source in Settings → Perforce."
                    : $"Didn't log in to Perforce as {who}: the password prompt was cancelled.", NoteKind.Warning);
                break;
            case PerforceKeeperEventKind.LoginFailed:
                _host.AddNote($"Couldn't log in to Perforce as {who}: {e.Message ?? "the login failed."}", NoteKind.Error);
                break;
            case PerforceKeeperEventKind.NeedsUserLogin:
                _host.AddNote($"Perforce needs you to log in as {who} yourself: run p4 login in a terminal, or log in with P4V. {e.Message} Claudette checks again every minute.", NoteKind.Warning);
                _services.Notifications.Notify(NotificationKind.NeedsInput, _host.DisplayName, "Perforce needs you to log in: run p4 login in a terminal, or log in with P4V.", _host.Id);
                break;
        }
    }

    /// <summary>Shows the password prompt and waits for the answer. Called from the keeper, off the UI thread.</summary>
    private async Task<PerforcePasswordAnswer?> AskForPerforcePasswordAsync(PerforcePasswordRequest request, bool offerToSave, CancellationToken cancellationToken)
    {
        var shown = new TaskCompletionSource<PerforcePasswordPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Dispatcher.Post(() =>
        {
            var prompt = new PerforcePasswordPrompt(request, offerToSave, _services.Perforce.Credentials.Name);
            Prompt?.Abandon();
            Prompt = prompt;
            // It waits on the user like a permission prompt: "Needs input", the badge, and a notification.
            _host.WaitOnUser(prompt);
            _services.Notifications.Notify(NotificationKind.NeedsInput, _host.DisplayName, $"Perforce needs your password to log in as {request.User} @ {request.Server}.", _host.Id);
            shown.SetResult(prompt);
        });
        var prompt = await shown.Task.ConfigureAwait(false);
        try
        {
            return await prompt.Answer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _services.Dispatcher.Post(() =>
            {
                prompt.Abandon();
                if (ReferenceEquals(Prompt, prompt))
                {
                    Prompt = null;
                }
                _host.Resolved(prompt);
            });
        }
    }

    /// <summary>Ends ticket handling: the session ended, the tab closed, or a new session starts.</summary>
    internal void Stop()
    {
        if (_perforceKeeper is null)
        {
            return;
        }
        _perforceKeeper.Dispose();
        _perforceKeeper = null;
        _perforceKeeperStarted = false;
        _perforceRetryPending = false;
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(StatusText));
        _host.InfoRowsChanged();
    }
}
