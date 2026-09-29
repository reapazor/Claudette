using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claudette.Core.Protocol;
using Claudette.Core.RemoteControl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.Sessions;

/// <summary>The session's process ended while a request was waiting for it.</summary>
public sealed class ClaudeSessionExitedException(TransportExit exit)
    : Exception($"Claude Code exited (code {exit.ExitCode?.ToString() ?? "unknown"}).")
{
    public TransportExit Exit { get; } = exit;
}

/// <summary>
/// One long-running Claude Code process in stream-json mode (DESIGN.md §13). Reads its output, turns it into
/// <see cref="Events"/>, answers its control requests, and sends messages and control requests to it.
/// </summary>
public sealed class ClaudeSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultControlTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Longer, because MCP servers start during initialization.</summary>
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(90);

    private readonly IClaudeTransport _transport;
    private readonly ControlChannel _control;
    private readonly ILogger _logger;
    private readonly ProtocolDiagnostics? _diagnostics;
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly ConcurrentDictionary<string, PermissionRequest> _pendingPermissions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingHooks = new();
    private HookCallbackRegistry _hooks = new([]);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;
    private int _unknownMessageCount;
    private int _protocolErrorCount;
    private SessionState _state = SessionState.Starting;

    /// <param name="diagnostics">Counts what Claude Code sends that Claudette doesn't know yet (DESIGN.md §16).</param>
    public ClaudeSession(IClaudeTransport transport, TimeProvider timeProvider, ILogger<ClaudeSession>? logger = null, ProtocolDiagnostics? diagnostics = null)
    {
        _transport = transport;
        _logger = logger ?? NullLogger<ClaudeSession>.Instance;
        _diagnostics = diagnostics;
        _control = new ControlChannel(transport.SendAsync, timeProvider);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Everything the session reports, in order. Completes after <see cref="SessionExited"/>.</summary>
    public ChannelReader<SessionEvent> Events => _events.Reader;

    public SessionState State => _state;

    public InitializeResult? Initialization { get; private set; }

    public string? SessionId { get; private set; }

    public string? Model { get; private set; }

    public string? PermissionMode { get; private set; }

    public string? ClaudeCodeVersion { get; private set; }

    public IReadOnlyList<string> Capabilities { get; private set; } = [];

    /// <summary>The <c>claude</c> process id, for the process monitor (DESIGN.md §4). Null for test transports.</summary>
    public int? ProcessId => _transport.ProcessId;

    /// <summary>Messages of a type Claudette doesn't know yet, skipped so far.</summary>
    public int UnknownMessageCount => _unknownMessageCount;

    /// <summary>Lines that couldn't be parsed, skipped so far.</summary>
    public int ProtocolErrorCount => _protocolErrorCount;

    /// <summary>Completes when the process has exited and every event has been published.</summary>
    public Task Completion => _readLoop;

    public Task<InitializeResult> InitializeAsync(CancellationToken cancellationToken = default) => InitializeAsync([], cancellationToken);

    /// <param name="hooks">
    /// Hook callbacks to register through the <c>hooks</c> field, as the Agent SDKs do; Claude Code calls them back with
    /// <c>hook_callback</c> control requests (DESIGN.md §13, "Hook callbacks").
    /// </param>
    public async Task<InitializeResult> InitializeAsync(IReadOnlyList<HookRegistration> hooks, CancellationToken cancellationToken = default)
    {
        _hooks = new HookCallbackRegistry(hooks);
        var response = await _control.RequestAsync(new JsonObject { ["subtype"] = "initialize", ["hooks"] = _hooks.Config }, InitializeTimeout, cancellationToken)
            .ConfigureAwait(false);
        Initialization = InitializeResult.Parse(response);
        PermissionMode ??= Initialization.CurrentPermissionMode;
        if (_state == SessionState.Starting)
        {
            SetState(SessionState.Idle);
        }
        return Initialization;
    }

    public ValueTask SendUserMessageAsync(string text, CancellationToken cancellationToken = default) =>
        SendUserMessageAsync(text, [], cancellationToken);

    /// <summary>Sends a message with attached images (DESIGN.md §5, "Attachments").</summary>
    public ValueTask SendUserMessageAsync(string text, IReadOnlyList<MessageImage> images, CancellationToken cancellationToken = default) =>
        SendUserMessageAsync(text, images, null, cancellationToken);

    /// <summary>Sends a message with attached images and quick suffixes (DESIGN.md §5, "Quick suffixes").</summary>
    public async ValueTask SendUserMessageAsync(string text, IReadOnlyList<MessageImage> images, string? suffix, CancellationToken cancellationToken = default)
    {
        await _transport.SendAsync(OutgoingMessages.UserMessage(text, images, suffix).ToJsonString(), cancellationToken).ConfigureAwait(false);
        if (_state == SessionState.Idle)
        {
            SetState(SessionState.Working);
        }
    }

    /// <summary>Stops the current turn. The turn still ends with a <see cref="TurnCompleted"/> event.</summary>
    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(new JsonObject { ["subtype"] = "interrupt" }, cancellationToken: cancellationToken);

    /// <summary>Switches model in place; the conversation is kept. Null returns to Claude Code's default model.</summary>
    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(new JsonObject { ["subtype"] = "set_model", ["model"] = model }, cancellationToken: cancellationToken);

    /// <summary>Sets the effort level from the next request. Null returns to the model's default.</summary>
    public Task SetEffortAsync(string? level, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            new JsonObject { ["subtype"] = "apply_flag_settings", ["settings"] = new JsonObject { ["effortLevel"] = level } },
            cancellationToken: cancellationToken);

    public async Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        await SendControlRequestAsync(new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = mode }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        PermissionMode = mode;
    }

    /// <summary>
    /// Asks Claude Code for an AI-generated session title (DESIGN.md §13, "Session naming"). Headless sessions don't
    /// get one on their own. One small model call. Undocumented request.
    /// </summary>
    public async Task<string?> GenerateSessionTitleAsync(string description, bool persist = true, CancellationToken cancellationToken = default)
    {
        var response = await SendControlRequestAsync(
            new JsonObject { ["subtype"] = "generate_session_title", ["description"] = description, ["persist"] = persist },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        return response.GetString("title");
    }

    /// <summary>Saves a custom session name, so <c>claude --resume &lt;name&gt;</c> finds it. Undocumented request.</summary>
    public Task RenameSessionAsync(string title, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(new JsonObject { ["subtype"] = "rename_session", ["title"] = title, ["source"] = "host" }, cancellationToken: cancellationToken);

    /// <summary>
    /// Connects the session to claude.ai and the Claude app, as SDK hosts such as the VS Code extension do (DESIGN.md
    /// §18, "Remote Control"). Claude Code checks the account first, then registers the session: the answer has its
    /// address (<see cref="RemoteControlProtocol.FromEnabled"/>), and an error says why it can't. Undocumented request.
    /// </summary>
    /// <param name="name">The session's title on claude.ai and in the Claude app.</param>
    public Task<JsonObject> EnableRemoteControlAsync(string? name, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(RemoteControlProtocol.EnableRequest(name), RemoteControlProtocol.Timeout, cancellationToken);

    /// <summary>Disconnects the session from claude.ai; it carries on here. Undocumented request.</summary>
    public Task DisableRemoteControlAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(RemoteControlProtocol.DisableRequest(), RemoteControlProtocol.Timeout, cancellationToken);

    /// <summary>How full the context window is. Claude Code counts tokens with the API's free counting endpoint.</summary>
    public async Task<ContextUsage> GetContextUsageAsync(CancellationToken cancellationToken = default) =>
        ContextUsage.Parse(await SendControlRequestAsync(new JsonObject { ["subtype"] = "get_context_usage" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

    /// <summary>Stops a background task (for example a <c>run_in_background</c> command) so Claude knows it ended.</summary>
    public Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(new JsonObject { ["subtype"] = "stop_task", ["task_id"] = taskId }, cancellationToken: cancellationToken);

    /// <summary>Sends any control request and returns Claude Code's response payload.</summary>
    public Task<JsonObject> SendControlRequestAsync(JsonObject request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _control.RequestAsync(request, timeout ?? DefaultControlTimeout, cancellationToken);

    /// <summary>Asks Claude Code to exit by closing its input, and ends it if it hasn't exited within <paramref name="grace"/>.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        _transport.CloseInput();
        try
        {
            await _transport.Completion.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Claude Code didn't exit within {Grace}; ending it.", grace);
            _transport.Terminate();
        }
        await _readLoop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_transport.Completion.IsCompleted)
        {
            await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var line in _transport.Output.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (!MessageParser.TryParse(line, out var message, out var error))
                {
                    Interlocked.Increment(ref _protocolErrorCount);
                    _diagnostics?.RecordParseError();
                    _logger.LogWarning("Skipped a line from Claude Code: {Error}", error);
                    Publish(new ProtocolError(line, error));
                    continue;
                }
                try
                {
                    _diagnostics?.RecordFields(message);
                    Handle(message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad message must never end the session (DESIGN.md §16).
                    Interlocked.Increment(ref _protocolErrorCount);
                    _diagnostics?.RecordParseError();
                    _logger.LogError(ex, "Failed to handle a '{Type}' message.", message.Type);
                    Publish(new ProtocolError(line, ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }

        var exit = await _transport.Completion.ConfigureAwait(false);
        _control.FailAll(new ClaudeSessionExitedException(exit));
        foreach (var request in _pendingPermissions.Values)
        {
            request.Cancel();
        }
        _pendingPermissions.Clear();
        foreach (var requestId in _pendingHooks.Keys)
        {
            if (_pendingHooks.TryRemove(requestId, out var hook))
            {
                CancelHook(hook);
            }
        }
        SetState(SessionState.Exited);
        Publish(new SessionExited(exit));
        _events.Writer.TryComplete();
    }

    private void Handle(ClaudeMessage message)
    {
        switch (message)
        {
            case ControlResponseMessage response:
                if (!_control.TryComplete(response))
                {
                    _logger.LogDebug("Control response {RequestId} had no waiting request.", response.RequestId);
                }
                break;

            case ControlRequestMessage request:
                HandleControlRequest(request);
                break;

            case ControlCancelRequestMessage cancel:
                if (_pendingPermissions.TryRemove(cancel.RequestId, out var cancelled))
                {
                    cancelled.Cancel();
                    Publish(new PermissionCancelled(cancel.RequestId));
                }
                else if (_pendingHooks.TryRemove(cancel.RequestId, out var hook))
                {
                    CancelHook(hook);
                }
                break;

            case SystemInitMessage init:
                SessionId = init.SessionId;
                Model = init.Model ?? Model;
                PermissionMode = init.PermissionMode ?? PermissionMode;
                ClaudeCodeVersion = init.ClaudeCodeVersion ?? ClaudeCodeVersion;
                Capabilities = init.Capabilities;
                Publish(new TurnStarted(init));
                if (_state == SessionState.Idle)
                {
                    SetState(SessionState.Working);
                }
                break;

            case SystemMessage system:
                if (system.Subtype == "status" && system.Raw.GetString("permissionMode") is { } mode)
                {
                    PermissionMode = mode;
                }
                Publish(new SystemNotice(system));
                break;

            case StreamEventMessage stream:
                if (stream.TextDelta is { } text)
                {
                    Publish(new TextDelta(text, stream.ParentToolUseId));
                }
                else if (stream.ThinkingDelta is { } thinking)
                {
                    Publish(new ThinkingDelta(thinking, stream.ParentToolUseId));
                }
                break;

            case AssistantMessage assistant:
                if (Auth.SignInErrors.IsSignInCategory(assistant.Error))
                {
                    Publish(new AuthenticationRequired(assistant.Content.OfType<TextBlock>().FirstOrDefault()?.Text));
                }
                Publish(new AssistantMessageReceived(assistant));
                break;

            case UserMessage user:
                if (user.LocalCommandOutput is { } output)
                {
                    Publish(new LocalCommandOutputReceived(output));
                }
                else
                {
                    Publish(new ToolResultsReceived(user));
                }
                break;

            case ResultMessage result:
                SessionId = result.SessionId ?? SessionId;
                Publish(new TurnCompleted(result));
                SetState(SessionState.Idle);
                break;

            case RateLimitEventMessage rateLimit:
                Publish(new RateLimitUpdated(rateLimit));
                break;

            case ToolProgressMessage progress:
                Publish(new ToolProgress(progress));
                break;

            case AuthStatusMessage auth:
                // Only sent with the hidden --enable-auth-status flag, which Claudette doesn't pass; it reports cloud
                // credential helpers such as awsAuthRefresh. One that failed still means Claude Code can't sign in.
                if (auth.Error is not null)
                {
                    Publish(new AuthenticationRequired(auth.Error));
                }
                break;

            case ConversationResetMessage reset:
                Publish(new ConversationReset(reset.Trigger));
                break;

            case AutocompactStateMessage autocompact:
                Publish(new AutocompactStateChanged(autocompact));
                break;

            case IgnoredMessage:
                break;

            case UnknownMessage unknown:
                Interlocked.Increment(ref _unknownMessageCount);
                _diagnostics?.RecordUnknownMessage(unknown.MessageType);
                _logger.LogDebug("Skipped unknown message type '{Type}'.", unknown.MessageType);
                Publish(new UnrecognizedMessage(unknown.MessageType, unknown.Raw));
                break;
        }
    }

    private void HandleControlRequest(ControlRequestMessage request)
    {
        if (request.Subtype == "hook_callback")
        {
            HandleHookCallback(request);
            return;
        }
        if (request.Subtype != "can_use_tool")
        {
            // SDK MCP servers aren't used. Answer so Claude Code doesn't wait forever.
            _logger.LogWarning("Unsupported control request '{Subtype}' from Claude Code.", request.Subtype);
            _ = RespondSafelyAsync(() => _control.RespondErrorAsync(request.RequestId, $"Unsupported control request: {request.Subtype}", _lifetime.Token));
            return;
        }

        var permission = new PermissionRequest(request);
        _pendingPermissions[request.RequestId] = permission;
        Publish(new PermissionRequested(permission));
        _ = AnswerPermissionAsync(permission);
    }

    /// <summary>
    /// Runs a registered hook callback off the read loop, and answers with its output. An unknown callback, or one
    /// that fails, gets an error answer, which Claude Code treats as a hook error and carries on. A call Claude Code
    /// withdraws (<c>control_cancel_request</c>) is cancelled and not answered.
    /// </summary>
    private void HandleHookCallback(ControlRequestMessage request)
    {
        if (request.Request.GetString("callback_id") is not { } callbackId || _hooks.Find(callbackId) is not { } callback)
        {
            _logger.LogWarning("Claude Code called back an unknown hook.");
            _ = RespondSafelyAsync(() => _control.RespondErrorAsync(request.RequestId, "No hook callback with that id.", _lifetime.Token));
            return;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pendingHooks[request.RequestId] = cancellation;
        _ = RunHookAsync(request.RequestId, callback, HookInput.Parse(request.Request), cancellation);
    }

    private async Task RunHookAsync(string requestId, HookCallback callback, HookInput input, CancellationTokenSource cancellation)
    {
        try
        {
            JsonObject output;
            try
            {
                output = await Task.Run(() => callback(input, cancellation.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A {Event} hook callback failed.", input.EventName);
                if (_pendingHooks.TryRemove(requestId, out _))
                {
                    await RespondSafelyAsync(() => _control.RespondErrorAsync(requestId, ex.Message, _lifetime.Token)).ConfigureAwait(false);
                }
                return;
            }
            if (_pendingHooks.TryRemove(requestId, out _))
            {
                await RespondSafelyAsync(() => _control.RespondAsync(requestId, output, _lifetime.Token)).ConfigureAwait(false);
            }
        }
        finally
        {
            _pendingHooks.TryRemove(requestId, out _);
            cancellation.Dispose();
        }
    }

    private static void CancelHook(CancellationTokenSource hook)
    {
        try
        {
            hook.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It just finished.
        }
    }

    private async Task AnswerPermissionAsync(PermissionRequest permission)
    {
        PermissionDecision decision;
        try
        {
            decision = await permission.Decision.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_pendingPermissions.TryRemove(permission.RequestId, out _))
        {
            await RespondSafelyAsync(() => _control.RespondAsync(permission.RequestId, decision.ToResponse(), _lifetime.Token)).ConfigureAwait(false);
        }
    }

    private async Task RespondSafelyAsync(Func<ValueTask> respond)
    {
        try
        {
            await respond().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Couldn't answer a control request; the process has probably exited.");
        }
    }

    private void SetState(SessionState state)
    {
        if (_state == state || _state == SessionState.Exited)
        {
            return;
        }
        _state = state;
        Publish(new StateChanged(state));
    }

    private void Publish(SessionEvent sessionEvent) => _events.Writer.TryWrite(sessionEvent);
}
