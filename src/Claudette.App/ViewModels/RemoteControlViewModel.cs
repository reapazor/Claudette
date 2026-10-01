using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Protocol;
using Claudette.Core.RemoteControl;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>What Remote Control needs from its tab.</summary>
internal interface IRemoteControlHost : ITabAreaHost
{
    /// <summary>The <c>/remote-control</c> fallback's own turn ended: the check-ins stop, as at the end of any turn.</summary>
    void CommandTurnEnded();

    /// <summary>Stops the tab's <c>claude</c> and starts a new one on the same session.</summary>
    Task RestartSessionAsync();
}

/// <summary>
/// The tab's connection to the Claude app, Remote Control (DESIGN.md §18): its switch, connecting and disconnecting,
/// and what Claude Code says about the connection.
/// </summary>
public sealed partial class RemoteControlViewModel : ViewModelBase
{
    /// <summary>What a prompt says when Claude Code withdraws it while the tab is connected: the phone answered it.</summary>
    public const string AnsweredInClaudeApp = "Answered in the Claude app";

    /// <summary>A change of the switch that waits for the running turn to end.</summary>
    private enum Change
    {
        None,
        Connect,
        Disconnect,
    }

    private readonly AppServices _services;
    private readonly IRemoteControlHost _host;

    private Change _waiting;

    /// <summary>The request to disconnect is out: Claude Code is closing the connection, whatever the switch says now.</summary>
    private bool _disconnecting;

    /// <summary>This session's Claude Code rejected the <c>remote_control</c> request: the tab uses <c>/remote-control</c> instead.</summary>
    private bool _usesCommand;

    /// <summary>The <c>/remote-control</c> fallback was sent: its turn is its answer, shown as a note rather than a reply.</summary>
    private bool _commandPending;

    private string? _commandReply;

    private string? _commandOutcome;

    /// <summary>The address this session last connected at, for a link Claude Code brings back by itself.</summary>
    private string? _url;

    /// <summary>Claudette stopped the turn or a subagent: a prompt withdrawn now wasn't answered in the Claude app.</summary>
    private bool _stoppedHere;

    internal RemoteControlViewModel(AppServices services, IRemoteControlHost host)
    {
        _services = services;
        _host = host;
    }

    // ---- The switch -----------------------------------------------------------------------------------------------

    /// <summary><b>Connect to the Claude app</b>: the tab connects whenever its session runs, restarts included.</summary>
    public bool IsOn => _host.State.RemoteControl;

    /// <summary>The account can use Remote Control; a tab that's on can always be turned off.</summary>
    public bool CanToggle => _host.State.RemoteControl || _services.RemoteControl.IsAvailable;

    /// <summary>The switch's tip: what it does, or why it's disabled.</summary>
    public string ToggleTip => !CanToggle && _services.RemoteControl.UnavailableReason is { } reason
        ? reason
        : "Use this tab from the Claude app on your phone, or at claude.ai/code, while it runs here";

    /// <summary><b>Connect to the Claude app</b> in the tab's menu.</summary>
    [RelayCommand(CanExecute = nameof(CanToggle))]
    private Task ToggleAsync() => SetAsync(!_host.State.RemoteControl);

    /// <summary>
    /// Turns the switch on or off. On connects now if Claude isn't working, or when the turn ends; a tab that isn't
    /// running connects when it starts. Off disconnects the same way. Refused while the account can't use it.
    /// </summary>
    public async Task SetAsync(bool on)
    {
        var state = _host.State;
        if (on == state.RemoteControl || on && !_services.RemoteControl.IsAvailable)
        {
            // The menu item ticked itself as it was clicked: reading the value again puts it back.
            OnPropertyChanged(nameof(IsOn));
            return;
        }
        state.RemoteControl = on;
        OnPropertyChanged(nameof(IsOn));
        OnSwitchChanged();
        _services.SaveState();
        if (on)
        {
            RequestConnect();
        }
        else
        {
            await RequestDisconnectAsync();
        }
    }

    /// <summary>The account changed, which can make Remote Control available or not (DESIGN.md §18).</summary>
    public void OnAvailabilityChanged()
    {
        OnSwitchChanged();
        if (_host.State.RemoteControl && _services.RemoteControl.IsAvailable && Status.State is RemoteControlState.NotConnected or RemoteControlState.Unavailable)
        {
            RequestConnect();
        }
    }

    private void OnSwitchChanged()
    {
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ToggleTip));
        OnPropertyChanged(nameof(IsLeaving));
        OnInfoChanged();
        ToggleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the row's tip and the info card say about the connection changed.</summary>
    private void OnInfoChanged()
    {
        OnPropertyChanged(nameof(StatusTip));
        OnPropertyChanged(nameof(Info));
        _host.InfoRowsChanged();
    }

    // ---- The connection -------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(ShowIcon), nameof(IsSettling), nameof(IsLeaving), nameof(StatusTip), nameof(HasUrl), nameof(Info))]
    [NotifyCanExecuteChangedFor(nameof(OpenInClaudeAppCommand))]
    public partial RemoteControlStatus Status { get; private set; } = RemoteControlStatus.NotConnected;

    partial void OnStatusChanged(RemoteControlStatus value)
    {
        if (value.Url is { } url)
        {
            _url = url;
        }
        // The computer stays awake while any tab is connected (DESIGN.md §18).
        _services.RemoteControl.SetTabConnected(_host.Id, value.IsConnected);
        _host.InfoRowsChanged();
    }

    public bool IsConnected => Status.IsConnected;

    /// <summary>The icon on the tab's row: connected, or on its way there.</summary>
    public bool ShowIcon => Status.State is RemoteControlState.Connected or RemoteControlState.Connecting;

    /// <summary>Connecting, or reconnecting: the row's icon is dimmed.</summary>
    public bool IsSettling => Status.State == RemoteControlState.Connecting || Status.Detail == RemoteControlProtocol.Reconnecting;

    /// <summary>
    /// Switched off but still connected, waiting for the turn to end, or Claude Code is closing the connection. The
    /// row's icon is dimmed further than while it settles.
    /// </summary>
    public bool IsLeaving => Status.IsConnected && (!_host.State.RemoteControl || _disconnecting);

    /// <summary>The row icon's tip.</summary>
    public string StatusTip => Status.State switch
    {
        RemoteControlState.Connecting => "Connecting to the Claude app…",
        RemoteControlState.Connected when _waiting == Change.Disconnect => "Connected to the Claude app. Disconnects when Claude finishes this turn.",
        RemoteControlState.Connected when IsLeaving => "Disconnecting from the Claude app…",
        RemoteControlState.Connected when Status.Detail == RemoteControlProtocol.Reconnecting => "Connected to the Claude app, reconnecting…",
        RemoteControlState.Connected => "Connected to the Claude app",
        _ => "Not connected to the Claude app",
    };

    /// <summary><b>Open in the Claude app</b>: the tab's session at claude.ai/code, which the Claude app opens on a phone.</summary>
    public bool HasUrl => Status is { IsConnected: true, Url: not null };

    [RelayCommand(CanExecute = nameof(HasUrl))]
    private Task OpenInClaudeAppAsync() => Status.Url is { } url ? _services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    /// <summary>The info card's "Claude app" row (DESIGN.md §4), or null when the tab has nothing to do with it.</summary>
    public string? Info => Status.State switch
    {
        RemoteControlState.Connected => "Connected"
            + (Status.Url is { } url ? $": {url}" : "")
            + (Status.Detail is { } detail ? $" ({detail})" : "")
            + (_waiting == Change.Disconnect ? ". Disconnects when Claude finishes this turn." : IsLeaving ? ". Disconnecting…" : ""),
        RemoteControlState.Connecting => "Connecting…",
        RemoteControlState.Unavailable => $"Not available: {Status.Detail}",
        _ when _waiting == Change.Connect => "Connects when Claude finishes this turn",
        _ when _host.State.RemoteControl && _services.RemoteControl.UnavailableReason is { } reason => $"Not available: {reason}",
        _ when _host.State.RemoteControl && _host.Session is null => "Connects when the tab starts",
        _ when Status.Detail is { } why => $"Not connected: {why}",
        _ when _host.State.RemoteControl => "Not connected",
        _ => null,
    };

    internal void AddInfoRows(List<InfoRow> rows)
    {
        if (Info is { } info)
        {
            rows.Add(new InfoRow("Claude app", info));
        }
    }

    /// <summary>The session just started: connect before any prompt goes out, if the switch is on.</summary>
    internal void ConnectOnStart(ClaudeSession session)
    {
        _waiting = Change.None;
        _usesCommand = false;
        if (!_host.State.RemoteControl)
        {
            return;
        }
        if (_services.RemoteControl.UnavailableReason is { } reason)
        {
            Status = new RemoteControlStatus(RemoteControlState.Unavailable, Detail: reason);
            _host.AddNote($"Not connecting to the Claude app: {reason}", NoteKind.Warning);
            return;
        }
        _ = ConnectAsync(session);
    }

    /// <summary>Connects now, or when the running turn ends; a tab that isn't running connects when it starts.</summary>
    private void RequestConnect()
    {
        if (_waiting == Change.Disconnect)
        {
            // Still connected: nothing to do once the turn ends.
            _waiting = Change.None;
            _host.AddNote("Staying connected to the Claude app.");
            OnInfoChanged();
            return;
        }
        // Claude Code already closing the connection connects again once it has.
        if (_host.Session is not { } session || _disconnecting || Status.State is RemoteControlState.Connected or RemoteControlState.Connecting)
        {
            OnInfoChanged();
            return;
        }
        if (session.State == SessionState.Working)
        {
            _waiting = Change.Connect;
            OnInfoChanged();
            return;
        }
        _ = _usesCommand ? SendCommandAsync(session) : ConnectAsync(session);
    }

    /// <summary>
    /// Sends the <c>remote_control</c> request, as SDK hosts do. Claude Code checks the account, registers the session
    /// with claude.ai and answers with its address, or says why it can't. A Claude Code that doesn't know the request
    /// gets the <c>/remote-control</c> command instead.
    /// </summary>
    private async Task ConnectAsync(ClaudeSession session)
    {
        _waiting = Change.None;
        Status = RemoteControlStatus.Connecting;
        RemoteControlStatus answer;
        try
        {
            answer = RemoteControlProtocol.FromEnabled(await session.EnableRemoteControlAsync(_host.DisplayName));
        }
        catch (ControlRequestException ex) when (RemoteControlProtocol.IsUnsupported(ex.Error))
        {
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _host.Session))
                {
                    _usesCommand = true;
                    if (session.State == SessionState.Working)
                    {
                        Status = RemoteControlStatus.NotConnected;
                        _waiting = Change.Connect;
                    }
                    else
                    {
                        _ = SendCommandAsync(session);
                    }
                }
            });
            return;
        }
        catch (ControlRequestException ex)
        {
            answer = new RemoteControlStatus(RemoteControlState.Unavailable, Detail: ex.Error);
        }
        catch (ClaudeSessionExitedException)
        {
            // The session ended; its exit resets the connection.
            return;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            answer = new RemoteControlStatus(RemoteControlState.Unavailable, Detail: ex.Message);
        }
        _services.Dispatcher.Post(() =>
        {
            if (ReferenceEquals(session, _host.Session))
            {
                OnConnectAnswer(answer);
            }
        });
    }

    /// <summary>
    /// The fallback: <c>/remote-control &lt;name&gt;</c> as a message of its own, sent only while Claude isn't working, so
    /// the next turn to end is its answer. The tab doesn't show it as something the user sent.
    /// </summary>
    private async Task SendCommandAsync(ClaudeSession session)
    {
        _waiting = Change.None;
        _commandPending = true;
        _commandReply = null;
        _commandOutcome = null;
        Status = RemoteControlStatus.Connecting;
        try
        {
            await session.SendUserMessageAsync($"{RemoteControlProtocol.Command} {_host.DisplayName}");
        }
        catch (Exception ex)
        {
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _host.Session))
                {
                    _commandPending = false;
                    OnConnectAnswer(new RemoteControlStatus(RemoteControlState.Unavailable, Detail: ex.Message));
                }
            });
        }
    }

    /// <summary>
    /// The events of the <c>/remote-control</c> fallback's own turn are its answer: its reply shows as a note, and the
    /// turn doesn't count as one (no summary, tokens, notification or unread dot). Returns whether it took the event.
    /// </summary>
    internal bool InterceptCommand(SessionEvent sessionEvent)
    {
        if (!_commandPending)
        {
            return false;
        }
        switch (sessionEvent)
        {
            case AssistantMessageReceived { Message: { ParentToolUseId: null } message }:
                if (string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text)) is { Length: > 0 } text)
                {
                    _commandReply = text;
                }
                _commandOutcome ??= message.Raw.GetObject("local_command_outcome")?.GetString("kind");
                return true;
            case LocalCommandOutputReceived local:
                _commandReply ??= local.Text;
                return true;
            case TextDelta { ParentToolUseId: null } or ThinkingDelta { ParentToolUseId: null }:
                return true;
            case TurnCompleted completed:
                _commandPending = false;
                _host.CommandTurnEnded();
                _host.State.SessionId = completed.Result.SessionId ?? _host.State.SessionId;
                OnConnectAnswer(RemoteControlProtocol.FromCommandReply(_commandReply ?? completed.Result.Result ?? "", _commandOutcome));
                return true;
            default:
                return false;
        }
    }

    /// <summary>What connecting came to: shown in the conversation as a note, with the session's link when there is one.</summary>
    private void OnConnectAnswer(RemoteControlStatus answer)
    {
        if (Status.State == RemoteControlState.Unavailable && answer.State == RemoteControlState.Unavailable && Status.Detail == answer.Detail)
        {
            // A policy Claude Code reported while connecting already said so.
            return;
        }
        Status = answer;
        switch (answer.State)
        {
            case RemoteControlState.Connected:
                // A reply Claudette couldn't read as either is shown as it is.
                _host.AddNote(answer.Url is null && answer.Detail is { } said ? said : "Connected to the Claude app.", NoteKind.Info, answer.Url);
                break;
            case RemoteControlState.Unavailable:
                _host.AddNote($"Couldn't connect to the Claude app: {answer.Detail}", NoteKind.Warning);
                break;
        }
        if (!_host.State.RemoteControl && answer.IsConnected)
        {
            // Turned off while it was connecting.
            _ = RequestDisconnectAsync();
        }
    }

    /// <summary>Disconnects now, or when the running turn ends. A tab still connecting disconnects once it's connected.</summary>
    private async Task RequestDisconnectAsync()
    {
        if (_waiting == Change.Connect)
        {
            _waiting = Change.None;
        }
        if (_host.Session is not { } session || Status.State != RemoteControlState.Connected)
        {
            if (Status.State != RemoteControlState.Connecting)
            {
                Status = RemoteControlStatus.NotConnected;
            }
            OnInfoChanged();
            return;
        }
        if (_disconnecting)
        {
            // Turned on and off again while Claude Code closes the connection: the request that's out is enough.
            OnInfoChanged();
            return;
        }
        if (session.State == SessionState.Working)
        {
            if (_waiting != Change.Disconnect)
            {
                _waiting = Change.Disconnect;
                _host.AddNote("Disconnecting from the Claude app when Claude finishes this turn.");
            }
            OnInfoChanged();
            return;
        }
        await DisconnectAsync(session);
    }

    /// <summary>
    /// <c>remote_control</c> with <c>enabled: false</c>: Claude Code disconnects and the session carries on here. If
    /// it can't say it did, Claude Code is restarted on the same session instead, which doesn't connect again. A tab
    /// switched back on in the meantime connects again once it's disconnected.
    /// </summary>
    private async Task DisconnectAsync(ClaudeSession session)
    {
        _waiting = Change.None;
        _disconnecting = true;
        try
        {
            await session.DisableRemoteControlAsync();
        }
        catch (ClaudeSessionExitedException)
        {
            // The session ended; its exit resets the connection.
            return;
        }
        catch (Exception ex) when (ex is ControlRequestException or TimeoutException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _host.Session))
                {
                    // Still connected, as far as Claudette knows.
                    _disconnecting = false;
                    OnPropertyChanged(nameof(IsLeaving));
                    OnInfoChanged();
                    if (!_host.State.RemoteControl)
                    {
                        _ = RestartToDisconnectAsync();
                    }
                }
            });
            return;
        }
        _services.Dispatcher.Post(() =>
        {
            if (ReferenceEquals(session, _host.Session))
            {
                _disconnecting = false;
                Status = RemoteControlStatus.NotConnected;
                _host.AddNote("Disconnected from the Claude app.");
                if (_host.State.RemoteControl)
                {
                    // Switched back on while Claude Code closed the connection.
                    RequestConnect();
                }
            }
        });
    }

    /// <summary>The fallback for disconnecting: a new <c>claude</c> on the same session, with the switch off.</summary>
    private async Task RestartToDisconnectAsync()
    {
        _host.AddNote("Restarting Claude Code to disconnect from the Claude app. The conversation carries on.");
        await _host.RestartSessionAsync();
    }

    /// <summary>
    /// A turn ended: a prompt withdrawn from now on can have been answered in the Claude app, and a change of the switch
    /// that waited for the turn happens now.
    /// </summary>
    internal void OnTurnCompleted(ClaudeSession session)
    {
        _stoppedHere = false;
        var waiting = _waiting;
        _waiting = Change.None;
        switch (waiting)
        {
            case Change.Connect when _host.State.RemoteControl:
                _ = _usesCommand ? SendCommandAsync(session) : ConnectAsync(session);
                break;
            case Change.Disconnect when !_host.State.RemoteControl:
                // No longer waiting: the tip and the info card say it's disconnecting.
                OnInfoChanged();
                _ = DisconnectAsync(session);
                break;
        }
    }

    /// <summary>
    /// What Claude Code reports about the connection: <c>system/bridge_state</c> (undocumented; a state Claudette doesn't
    /// know changes nothing) and <c>system/worker_shutting_down</c>.
    /// </summary>
    internal void OnNotice(SystemMessage message)
    {
        var before = Status;
        var next = message.Subtype switch
        {
            "bridge_state" => RemoteControlProtocol.AfterBridgeState(Status, _url, message.Raw),
            "worker_shutting_down" => RemoteControlProtocol.AfterWorkerShuttingDown(Status, message.Raw, leaving: _disconnecting),
            _ => null,
        };
        if (next is null)
        {
            return;
        }
        Status = next;
        if (next.State == RemoteControlState.Unavailable && RemoteControlProtocol.SaysTurnedOffByPolicy(message.Subtype, message.Raw, leaving: _disconnecting))
        {
            // The other tabs, and this one when it starts again, don't try to connect again.
            _services.RemoteControl.OnTurnedOffByPolicy(next.Detail ?? RemoteControlProtocol.TurnedOffByPolicy);
        }
        if (before.IsConnected && !next.IsConnected)
        {
            _host.AddNote(next.State == RemoteControlState.Unavailable
                ? $"Remote Control stopped: {next.Detail}"
                : $"Disconnected from the Claude app: {next.Detail}", NoteKind.Warning);
        }
        else if (before.State == RemoteControlState.Connecting && next.State == RemoteControlState.Unavailable)
        {
            // Claude Code's answer to connecting says the same, and isn't shown again.
            _host.AddNote($"Couldn't connect to the Claude app: {next.Detail}", NoteKind.Warning);
        }
        else if (!before.IsConnected && next.IsConnected)
        {
            _host.AddNote("Connected to the Claude app again.", NoteKind.Info, next.Url);
        }
    }

    /// <summary>The process stopped or is about to: nothing is connected any more.</summary>
    internal void Reset()
    {
        _waiting = Change.None;
        _disconnecting = false;
        _commandPending = false;
        _usesCommand = false;
        Status = RemoteControlStatus.NotConnected;
        _url = null;
    }

    /// <summary>Claudette stopped the turn or a subagent: a prompt Claude Code withdraws now wasn't answered in the Claude app.</summary>
    internal void OnStoppedHere() => _stoppedHere = true;

    /// <summary>What a prompt Claude Code withdrew says: answered in the Claude app, if it can have been.</summary>
    internal string? WithdrawnPromptOutcome() => IsConnected && !_stoppedHere ? AnsweredInClaudeApp : null;
}
