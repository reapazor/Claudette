using Claudette.App.Conversation;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.ViewModels;

/// <summary>
/// This tab while Claude Code needs a sign-in (DESIGN.md §11): messages sent meanwhile are held and delivered once
/// signed in, and a session that failed restarts with <c>--resume</c>.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>A message on its way, with the images attached to it.</summary>
    private sealed record PendingMessage(string Text, IReadOnlyList<MessageImage> Images);

    /// <summary>Messages sent while Claude Code needed a sign-in, in the order they were sent.</summary>
    private readonly List<PendingMessage> _heldForSignIn = [];

    /// <summary>
    /// Messages Claude Code has been given that nothing has come back for yet. If the sign-in error comes first, they
    /// never reached the model, so they're sent again after the sign-in.
    /// </summary>
    private readonly List<PendingMessage> _awaitingReply = [];

    /// <summary>The note about held messages was shown, for this sign-in.</summary>
    private bool _heldNoted;

    /// <summary>This tab failed because Claude Code needs a sign-in, and starts again after one.</summary>
    public bool IsWaitingForSignIn => _restartAfterSignIn;

    /// <summary>Not started again until the sign-in, since it would only fail the same way.</summary>
    private bool WaitsForSignIn => _restartAfterSignIn && _shell.NeedsSignIn;

    /// <summary>The messages waiting for a sign-in.</summary>
    public IReadOnlyList<string> HeldMessages => [.. _heldForSignIn.Select(m => m.Text)];

    /// <summary>Holds <paramref name="message"/> if Claude Code needs a sign-in. Returns whether it was held.</summary>
    private bool HoldForSignIn(string message, IReadOnlyList<MessageImage>? images)
    {
        if (!_shell.NeedsSignIn)
        {
            return false;
        }
        _heldForSignIn.Add(new PendingMessage(message, images ?? []));
        NoteHeldMessages();
        return true;
    }

    /// <summary>Says once per sign-in that messages are being kept.</summary>
    private void NoteHeldMessages()
    {
        if (_heldForSignIn.Count > 0 && !_heldNoted)
        {
            _heldNoted = true;
            _conversation.AddNote("Claude Code needs you to sign in. Your messages are kept, and go to Claude once you've signed in.", NoteKind.Warning);
        }
    }

    /// <summary>Notes what Claude Code has answered, so a sign-in error knows which messages never got through.</summary>
    private void TrackReplies(SessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case TextDelta or ThinkingDelta or ToolResultsReceived or AssistantMessageReceived { Message.Error: null }:
            case TurnCompleted { Result.IsError: false }:
                _awaitingReply.Clear();
                break;
        }
    }

    /// <summary>
    /// The session reported that Claude Code needs a sign-in: an <c>assistant</c> message with a sign-in error, or an
    /// <c>auth_status</c> error. The messages it didn't answer wait for the sign-in with the ones sent after them.
    /// </summary>
    private void OnAuthenticationRequired()
    {
        _restartAfterSignIn = true;
        _heldForSignIn.InsertRange(0, _awaitingReply);
        _awaitingReply.Clear();
        NoteHeldMessages();
        _shell.OnAuthenticationRequired();
    }

    /// <summary>The session couldn't start because Claude Code isn't signed in: it starts again after the sign-in.</summary>
    private void OnStartFailedForSignIn(Exception ex)
    {
        _restartAfterSignIn = true;
        Status = TabStatus.Error;
        var said = ex is ClaudeSessionExitedException { Exit.StandardErrorTail: { Length: > 0 } tail }
            ? tail.Trim().Split('\n')[^1].Trim()
            : ex.Message;
        _conversation.AddNote($"Claude Code needs you to sign in before this tab can start (it said \"{said}\"). It starts again once you've signed in.", NoteKind.Warning);
        _shell.OnAuthenticationRequired();
    }

    /// <summary>Signed out from Claudette: if this tab is running, it restarts on its session after the next sign-in.</summary>
    public void OnSignedOut()
    {
        if (_session is not null)
        {
            _restartAfterSignIn = true;
        }
    }

    /// <summary>
    /// After a sign-in: a session that failed, or ran while signed out, restarts on the same session, then the held
    /// messages go out in order.
    /// </summary>
    public async Task OnSignedInAgainAsync()
    {
        if (_restartAfterSignIn)
        {
            _restartAfterSignIn = false;
            OnPropertyChanged(nameof(StatusTip));
            if (_session is not null || Status == TabStatus.Error)
            {
                await StopSessionAsync();
                if (Status == TabStatus.Error)
                {
                    Status = TabStatus.NotStarted;
                }
                _conversation.AddNote("Signed in. Picking the session up again.");
                await EnsureStartedAsync();
            }
        }
        _heldNoted = false;
        if (_heldForSignIn.Count == 0)
        {
            return;
        }
        var held = _heldForSignIn.ToArray();
        _heldForSignIn.Clear();
        _conversation.AddNote(held.Length == 1 ? "Signed in. Sending your message." : $"Signed in. Sending your {held.Length} messages.");
        foreach (var message in held)
        {
            await SendRawAsync(message.Text, message.Images);
        }
        _ = RequestTitleAsync();
    }
}
