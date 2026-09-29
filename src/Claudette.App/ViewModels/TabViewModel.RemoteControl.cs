using Claudette.App.Conversation;
using Claudette.Core.Protocol;
using Claudette.Core.RemoteControl;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab's connection to the Claude app, Remote Control (DESIGN.md §18): its switch, connecting and disconnecting,
/// and what Claude Code says about the connection.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>What a prompt says when Claude Code withdraws it while the tab is connected: the phone answered it.</summary>
    public const string AnsweredInClaudeApp = "Answered in the Claude app";

    /// <summary>A change of the switch that waits for the running turn to end.</summary>
    private enum RemoteChange
    {
        None,
        Connect,
        Disconnect,
    }

    private RemoteChange _remoteWaiting;

    /// <summary>This session's Claude Code rejected the <c>remote_control</c> request: the tab uses <c>/remote-control</c> instead.</summary>
    private bool _remoteUsesCommand;

    /// <summary>The <c>/remote-control</c> fallback was sent: its turn is its answer, shown as a note rather than a reply.</summary>
    private bool _remoteCommandPending;

    private string? _remoteCommandReply;

    private string? _remoteCommandOutcome;

    /// <summary>The address this session last connected at, for a link Claude Code brings back by itself.</summary>
    private string? _remoteUrl;

    /// <summary>Claudette stopped the turn or a subagent: a prompt withdrawn now wasn't answered in the Claude app.</summary>
    private bool _stoppedHere;

    // ---- The switch -----------------------------------------------------------------------------------------------

    /// <summary><b>Connect to the Claude app</b>: the tab connects whenever its session runs, restarts included.</summary>
    public bool RemoteControl => State.RemoteControl;

    /// <summary>The account can use Remote Control; a tab that's on can always be turned off.</summary>
    public bool CanToggleRemoteControl => State.RemoteControl || _services.RemoteControl.IsAvailable;

    /// <summary>The switch's tip: what it does, or why it's disabled.</summary>
    public string RemoteControlTip => !CanToggleRemoteControl && _services.RemoteControl.UnavailableReason is { } reason
        ? reason
        : "Use this tab from the Claude app on your phone, or at claude.ai/code, while it runs here";

    /// <summary><b>Connect to the Claude app</b> in the tab's menu.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleRemoteControl))]
    private Task ToggleRemoteControlAsync() => SetRemoteControlAsync(!State.RemoteControl);

    /// <summary>
    /// Turns the switch on or off. On connects now if Claude isn't working, or when the turn ends; a tab that isn't
    /// running connects when it starts. Off disconnects the same way. Refused while the account can't use it.
    /// </summary>
    public async Task SetRemoteControlAsync(bool on)
    {
        if (on == State.RemoteControl || on && !_services.RemoteControl.IsAvailable)
        {
            // The menu item ticked itself as it was clicked: reading the value again puts it back.
            OnPropertyChanged(nameof(RemoteControl));
            return;
        }
        State.RemoteControl = on;
        OnPropertyChanged(nameof(RemoteControl));
        OnRemoteSwitchChanged();
        _services.SaveState();
        if (on)
        {
            RequestRemoteConnect();
        }
        else
        {
            await RequestRemoteDisconnectAsync();
        }
    }

    /// <summary>The account changed, which can make Remote Control available or not (DESIGN.md §18).</summary>
    public void OnRemoteControlAvailabilityChanged()
    {
        OnRemoteSwitchChanged();
        if (State.RemoteControl && _services.RemoteControl.IsAvailable && Remote.State is RemoteControlState.NotConnected or RemoteControlState.Unavailable)
        {
            RequestRemoteConnect();
        }
    }

    private void OnRemoteSwitchChanged()
    {
        OnPropertyChanged(nameof(CanToggleRemoteControl));
        OnPropertyChanged(nameof(RemoteControlTip));
        OnPropertyChanged(nameof(RemoteInfo));
        OnPropertyChanged(nameof(InfoRows));
        ToggleRemoteControlCommand.NotifyCanExecuteChanged();
    }

    // ---- The connection -------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteConnected), nameof(ShowRemoteIcon), nameof(IsRemoteSettling), nameof(RemoteTip), nameof(HasRemoteUrl), nameof(RemoteInfo), nameof(InfoRows))]
    [NotifyCanExecuteChangedFor(nameof(OpenInClaudeAppCommand))]
    public partial RemoteControlStatus Remote { get; private set; } = RemoteControlStatus.NotConnected;

    partial void OnRemoteChanged(RemoteControlStatus value)
    {
        if (value.Url is { } url)
        {
            _remoteUrl = url;
        }
        // The computer stays awake while any tab is connected (DESIGN.md §18).
        _services.RemoteControl.SetTabConnected(Id, value.IsConnected);
    }

    public bool IsRemoteConnected => Remote.IsConnected;

    /// <summary>The icon on the tab's row: connected, or on its way there.</summary>
    public bool ShowRemoteIcon => Remote.State is RemoteControlState.Connected or RemoteControlState.Connecting;

    /// <summary>Connecting, or reconnecting: the row's icon is dimmed.</summary>
    public bool IsRemoteSettling => Remote.State == RemoteControlState.Connecting || Remote.Detail == RemoteControlProtocol.Reconnecting;

    /// <summary>The row icon's tip.</summary>
    public string RemoteTip => Remote.State switch
    {
        RemoteControlState.Connecting => "Connecting to the Claude app…",
        RemoteControlState.Connected when Remote.Detail == RemoteControlProtocol.Reconnecting => "Connected to the Claude app, reconnecting…",
        RemoteControlState.Connected => "Connected to the Claude app",
        _ => "Not connected to the Claude app",
    };

    /// <summary><b>Open in the Claude app</b>: the tab's session at claude.ai/code, which the Claude app opens on a phone.</summary>
    public bool HasRemoteUrl => Remote is { IsConnected: true, Url: not null };

    [RelayCommand(CanExecute = nameof(HasRemoteUrl))]
    private Task OpenInClaudeAppAsync() => Remote.Url is { } url ? _services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    /// <summary>The info card's "Claude app" row (DESIGN.md §4), or null when the tab has nothing to do with it.</summary>
    public string? RemoteInfo => Remote.State switch
    {
        RemoteControlState.Connected => "Connected"
            + (Remote.Url is { } url ? $": {url}" : "")
            + (Remote.Detail is { } detail ? $" ({detail})" : "")
            + (_remoteWaiting == RemoteChange.Disconnect ? ". Disconnects when Claude finishes this turn." : ""),
        RemoteControlState.Connecting => "Connecting…",
        RemoteControlState.Unavailable => $"Not available: {Remote.Detail}",
        _ when _remoteWaiting == RemoteChange.Connect => "Connects when Claude finishes this turn",
        _ when State.RemoteControl && _services.RemoteControl.UnavailableReason is { } reason => $"Not available: {reason}",
        _ when State.RemoteControl && _session is null => "Connects when the tab starts",
        _ when Remote.Detail is { } why => $"Not connected: {why}",
        _ when State.RemoteControl => "Not connected",
        _ => null,
    };

    private void AddRemoteControlRows(List<InfoRow> rows)
    {
        if (RemoteInfo is { } info)
        {
            rows.Add(new InfoRow("Claude app", info));
        }
    }

    /// <summary>The session just started: connect before any prompt goes out, if the switch is on.</summary>
    private void ConnectRemoteOnStart(ClaudeSession session)
    {
        _remoteWaiting = RemoteChange.None;
        _remoteUsesCommand = false;
        if (!State.RemoteControl)
        {
            return;
        }
        if (_services.RemoteControl.UnavailableReason is { } reason)
        {
            Remote = new RemoteControlStatus(RemoteControlState.Unavailable, Detail: reason);
            _conversation.AddNote($"Not connecting to the Claude app: {reason}", NoteKind.Warning);
            return;
        }
        _ = ConnectRemoteAsync(session);
    }

    /// <summary>Connects now, or when the running turn ends; a tab that isn't running connects when it starts.</summary>
    private void RequestRemoteConnect()
    {
        if (_remoteWaiting == RemoteChange.Disconnect)
        {
            // Still connected: nothing to do once the turn ends.
            _remoteWaiting = RemoteChange.None;
            OnPropertyChanged(nameof(RemoteInfo));
            OnPropertyChanged(nameof(InfoRows));
            return;
        }
        if (_session is not { } session || Remote.State is RemoteControlState.Connected or RemoteControlState.Connecting)
        {
            OnPropertyChanged(nameof(RemoteInfo));
            OnPropertyChanged(nameof(InfoRows));
            return;
        }
        if (session.State == SessionState.Working)
        {
            _remoteWaiting = RemoteChange.Connect;
            OnPropertyChanged(nameof(RemoteInfo));
            OnPropertyChanged(nameof(InfoRows));
            return;
        }
        _ = _remoteUsesCommand ? SendRemoteCommandAsync(session) : ConnectRemoteAsync(session);
    }

    /// <summary>
    /// Sends the <c>remote_control</c> request, as SDK hosts do. Claude Code checks the account, registers the session
    /// with claude.ai and answers with its address, or says why it can't. A Claude Code that doesn't know the request
    /// gets the <c>/remote-control</c> command instead.
    /// </summary>
    private async Task ConnectRemoteAsync(ClaudeSession session)
    {
        _remoteWaiting = RemoteChange.None;
        Remote = RemoteControlStatus.Connecting;
        RemoteControlStatus answer;
        try
        {
            answer = RemoteControlProtocol.FromEnabled(await session.EnableRemoteControlAsync(DisplayName));
        }
        catch (ControlRequestException ex) when (RemoteControlProtocol.IsUnsupported(ex.Error))
        {
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _session))
                {
                    _remoteUsesCommand = true;
                    if (session.State == SessionState.Working)
                    {
                        Remote = RemoteControlStatus.NotConnected;
                        _remoteWaiting = RemoteChange.Connect;
                    }
                    else
                    {
                        _ = SendRemoteCommandAsync(session);
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
            if (ReferenceEquals(session, _session))
            {
                OnRemoteConnectAnswer(answer);
            }
        });
    }

    /// <summary>
    /// The fallback: <c>/remote-control &lt;name&gt;</c> as a message of its own, sent only while Claude isn't working, so
    /// the next turn to end is its answer. The tab doesn't show it as something the user sent.
    /// </summary>
    private async Task SendRemoteCommandAsync(ClaudeSession session)
    {
        _remoteWaiting = RemoteChange.None;
        _remoteCommandPending = true;
        _remoteCommandReply = null;
        _remoteCommandOutcome = null;
        Remote = RemoteControlStatus.Connecting;
        try
        {
            await session.SendUserMessageAsync($"{RemoteControlProtocol.Command} {DisplayName}");
        }
        catch (Exception ex)
        {
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _session))
                {
                    _remoteCommandPending = false;
                    OnRemoteConnectAnswer(new RemoteControlStatus(RemoteControlState.Unavailable, Detail: ex.Message));
                }
            });
        }
    }

    /// <summary>
    /// The events of the <c>/remote-control</c> fallback's own turn are its answer: its reply shows as a note, and the
    /// turn doesn't count as one (no summary, tokens, notification or unread dot). Returns whether it took the event.
    /// </summary>
    private bool InterceptRemoteCommand(SessionEvent sessionEvent)
    {
        if (!_remoteCommandPending)
        {
            return false;
        }
        switch (sessionEvent)
        {
            case AssistantMessageReceived { Message: { ParentToolUseId: null } message }:
                if (string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text)) is { Length: > 0 } text)
                {
                    _remoteCommandReply = text;
                }
                _remoteCommandOutcome ??= message.Raw.GetObject("local_command_outcome")?.GetString("kind");
                return true;
            case LocalCommandOutputReceived local:
                _remoteCommandReply ??= local.Text;
                return true;
            case TextDelta { ParentToolUseId: null } or ThinkingDelta { ParentToolUseId: null }:
                return true;
            case TurnCompleted completed:
                _remoteCommandPending = false;
                _checkIns.TurnEnded();
                State.SessionId = completed.Result.SessionId ?? State.SessionId;
                OnRemoteConnectAnswer(RemoteControlProtocol.FromCommandReply(_remoteCommandReply ?? completed.Result.Result ?? "", _remoteCommandOutcome));
                return true;
            default:
                return false;
        }
    }

    /// <summary>What connecting came to: shown in the conversation as a note, with the session's link when there is one.</summary>
    private void OnRemoteConnectAnswer(RemoteControlStatus answer)
    {
        if (Remote.State == RemoteControlState.Unavailable && answer.State == RemoteControlState.Unavailable && Remote.Detail == answer.Detail)
        {
            // A policy Claude Code reported while connecting already said so.
            return;
        }
        Remote = answer;
        switch (answer.State)
        {
            case RemoteControlState.Connected:
                // A reply Claudette couldn't read as either is shown as it is.
                _conversation.AddNote(answer.Url is null && answer.Detail is { } said ? said : "Connected to the Claude app.", NoteKind.Info, answer.Url);
                break;
            case RemoteControlState.Unavailable:
                _conversation.AddNote($"Couldn't connect to the Claude app: {answer.Detail}", NoteKind.Warning);
                break;
        }
        if (!State.RemoteControl && answer.IsConnected)
        {
            // Turned off while it was connecting.
            _ = RequestRemoteDisconnectAsync();
        }
    }

    /// <summary>Disconnects now, or when the running turn ends. A tab still connecting disconnects once it's connected.</summary>
    private async Task RequestRemoteDisconnectAsync()
    {
        if (_remoteWaiting == RemoteChange.Connect)
        {
            _remoteWaiting = RemoteChange.None;
        }
        if (_session is not { } session || Remote.State != RemoteControlState.Connected)
        {
            if (Remote.State != RemoteControlState.Connecting)
            {
                Remote = RemoteControlStatus.NotConnected;
            }
            OnPropertyChanged(nameof(RemoteInfo));
            OnPropertyChanged(nameof(InfoRows));
            return;
        }
        if (session.State == SessionState.Working)
        {
            _remoteWaiting = RemoteChange.Disconnect;
            OnPropertyChanged(nameof(RemoteInfo));
            OnPropertyChanged(nameof(InfoRows));
            return;
        }
        await DisconnectRemoteAsync(session);
    }

    /// <summary>
    /// <c>remote_control</c> with <c>enabled: false</c>: Claude Code disconnects and the session carries on here. If
    /// it can't say it did, Claude Code is restarted on the same session instead, which doesn't connect again.
    /// </summary>
    private async Task DisconnectRemoteAsync(ClaudeSession session)
    {
        _remoteWaiting = RemoteChange.None;
        try
        {
            await session.DisableRemoteControlAsync();
        }
        catch (ClaudeSessionExitedException)
        {
            return;
        }
        catch (Exception ex) when (ex is ControlRequestException or TimeoutException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _session) && !State.RemoteControl)
                {
                    _ = RestartToDisconnectAsync();
                }
            });
            return;
        }
        _services.Dispatcher.Post(() =>
        {
            if (ReferenceEquals(session, _session) && !State.RemoteControl)
            {
                Remote = RemoteControlStatus.NotConnected;
                _conversation.AddNote("Disconnected from the Claude app.");
            }
        });
    }

    /// <summary>The fallback for disconnecting: a new <c>claude</c> on the same session, with the switch off.</summary>
    private async Task RestartToDisconnectAsync()
    {
        _conversation.AddNote("Restarting Claude Code to disconnect from the Claude app. The conversation carries on.");
        await StopSessionAsync();
        await EnsureStartedAsync();
    }

    /// <summary>A turn ended: a change of the switch that waited for it happens now.</summary>
    private void RunWaitingRemoteChange(ClaudeSession session)
    {
        var waiting = _remoteWaiting;
        _remoteWaiting = RemoteChange.None;
        switch (waiting)
        {
            case RemoteChange.Connect when State.RemoteControl:
                _ = _remoteUsesCommand ? SendRemoteCommandAsync(session) : ConnectRemoteAsync(session);
                break;
            case RemoteChange.Disconnect when !State.RemoteControl:
                _ = DisconnectRemoteAsync(session);
                break;
        }
    }

    /// <summary>
    /// What Claude Code reports about the connection: <c>system/bridge_state</c> (undocumented; a state Claudette doesn't
    /// know changes nothing) and <c>system/worker_shutting_down</c>.
    /// </summary>
    private void OnRemoteNotice(SystemMessage message)
    {
        var before = Remote;
        var next = message.Subtype switch
        {
            "bridge_state" => RemoteControlProtocol.AfterBridgeState(Remote, _remoteUrl, message.Raw),
            "worker_shutting_down" => RemoteControlProtocol.AfterWorkerShuttingDown(Remote, message.Raw),
            _ => null,
        };
        if (next is null)
        {
            return;
        }
        Remote = next;
        if (before.IsConnected && !next.IsConnected)
        {
            _conversation.AddNote(next.State == RemoteControlState.Unavailable
                ? $"Remote Control stopped: {next.Detail}"
                : $"Disconnected from the Claude app: {next.Detail}", NoteKind.Warning);
        }
        else if (before.State == RemoteControlState.Connecting && next.State == RemoteControlState.Unavailable)
        {
            // Claude Code's answer to connecting says the same, and isn't shown again.
            _conversation.AddNote($"Couldn't connect to the Claude app: {next.Detail}", NoteKind.Warning);
        }
        else if (!before.IsConnected && next.IsConnected)
        {
            _conversation.AddNote("Connected to the Claude app again.", NoteKind.Info, next.Url);
        }
    }

    /// <summary>The process stopped or is about to: nothing is connected any more.</summary>
    private void ResetRemote()
    {
        _remoteWaiting = RemoteChange.None;
        _remoteCommandPending = false;
        _remoteUsesCommand = false;
        Remote = RemoteControlStatus.NotConnected;
        _remoteUrl = null;
    }

    /// <summary>What a prompt Claude Code withdrew says: answered in the Claude app, if it can have been.</summary>
    private string? WithdrawnPromptOutcome() => IsRemoteConnected && !_stoppedHere ? AnsweredInClaudeApp : null;
}
