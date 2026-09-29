using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claudette.Core.Protocol;
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
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly ConcurrentDictionary<string, PermissionRequest> _pendingPermissions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;
    private int _unknownMessageCount;
    private int _protocolErrorCount;
    private SessionState _state = SessionState.Starting;

    public ClaudeSession(IClaudeTransport transport, TimeProvider timeProvider, ILogger<ClaudeSession>? logger = null)
    {
        _transport = transport;
        _logger = logger ?? NullLogger<ClaudeSession>.Instance;
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

    /// <summary>Messages of a type Claudette doesn't know yet, skipped so far.</summary>
    public int UnknownMessageCount => _unknownMessageCount;

    /// <summary>Lines that couldn't be parsed, skipped so far.</summary>
    public int ProtocolErrorCount => _protocolErrorCount;

    /// <summary>Completes when the process has exited and every event has been published.</summary>
    public Task Completion => _readLoop;

    public async Task<InitializeResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var response = await _control.RequestAsync(new JsonObject { ["subtype"] = "initialize", ["hooks"] = null }, InitializeTimeout, cancellationToken)
            .ConfigureAwait(false);
        Initialization = InitializeResult.Parse(response);
        PermissionMode ??= Initialization.CurrentPermissionMode;
        if (_state == SessionState.Starting)
        {
            SetState(SessionState.Idle);
        }
        return Initialization;
    }

    public async ValueTask SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        await _transport.SendAsync(OutgoingMessages.UserText(text).ToJsonString(), cancellationToken).ConfigureAwait(false);
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

    /// <summary>How full the context window is. Claude Code counts tokens with the API's free counting endpoint.</summary>
    public async Task<ContextUsage> GetContextUsageAsync(CancellationToken cancellationToken = default) =>
        ContextUsage.Parse(await SendControlRequestAsync(new JsonObject { ["subtype"] = "get_context_usage" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

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
                    _logger.LogWarning("Skipped a line from Claude Code: {Error}", error);
                    Publish(new ProtocolError(line, error));
                    continue;
                }
                try
                {
                    Handle(message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad message must never end the session (DESIGN.md §16).
                    Interlocked.Increment(ref _protocolErrorCount);
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
                if (assistant.Error == "authentication_failed")
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

            case AuthStatusMessage auth:
                if (auth.Error is not null)
                {
                    Publish(new AuthenticationRequired(auth.Error));
                }
                break;

            case ConversationResetMessage reset:
                Publish(new ConversationReset(reset.Trigger));
                break;

            case UnknownMessage unknown:
                Interlocked.Increment(ref _unknownMessageCount);
                _logger.LogDebug("Skipped unknown message type '{Type}'.", unknown.MessageType);
                Publish(new UnrecognizedMessage(unknown.MessageType));
                break;
        }
    }

    private void HandleControlRequest(ControlRequestMessage request)
    {
        if (request.Subtype != "can_use_tool")
        {
            // Hook callbacks and SDK MCP servers aren't used yet. Answer so Claude Code doesn't wait forever.
            _logger.LogWarning("Unsupported control request '{Subtype}' from Claude Code.", request.Subtype);
            _ = RespondSafelyAsync(() => _control.RespondErrorAsync(request.RequestId, $"Unsupported control request: {request.Subtype}", _lifetime.Token));
            return;
        }

        var permission = new PermissionRequest(request);
        _pendingPermissions[request.RequestId] = permission;
        Publish(new PermissionRequested(permission));
        _ = AnswerPermissionAsync(permission);
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
