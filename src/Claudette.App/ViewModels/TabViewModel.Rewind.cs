using System.Globalization;
using Claudette.App.Conversation;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// Going back to an earlier message (DESIGN.md §5, "Rewind and branch"): editing it and sending it again, branching a
/// new tab from just before it, and putting files back as they were before it. Claude Code does the work: a resume
/// with <c>--resume-session-at</c> keeps the conversation up to a point, and <c>rewind_files</c> puts back the files it
/// kept copies of.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>What the next start says about the copy it opens, instead of "Opened as a copy".</summary>
    private string? _forkNote;

    /// <summary>Asks the view to focus the composer, with the caret at the end: a message is back in it to edit.</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>
    /// The message can be gone back to: it's in this tab's conversation, and there's a point before it to resume at,
    /// or it's the conversation's first, before which there's nothing to keep.
    /// </summary>
    private bool HasPointBefore(UserMessageItem message) =>
        Items.Contains(message) && (message.ResumeAt is not null || !Items.TakeWhile(i => !ReferenceEquals(i, message)).OfType<UserMessageItem>().Any());

    private bool IsLastPrompt(UserMessageItem message) =>
        !Items.SkipWhile(i => !ReferenceEquals(i, message)).Skip(1).OfType<UserMessageItem>().Any();

    /// <summary>Why the tab can't go back now, or null when it can.</summary>
    private string? CantGoBackReason()
    {
        if (IsReadOnly)
        {
            return "This session continued on another machine.";
        }
        if (IsWorking)
        {
            return "Claude is working. Stop the turn, or wait for it to end, to go back to an earlier message.";
        }
        if (Status == TabStatus.Starting)
        {
            return "Claude Code is still starting.";
        }
        return null;
    }

    /// <summary>
    /// <b>Edit and resend</b>: the conversation goes back to just before the message, which goes back in the composer to
    /// change and send. The tab carries on as a copy of the session, so the session as it was stays in History. Files
    /// Claude changed since can be put back too, when Claude Code kept copies of them.
    /// </summary>
    [RelayCommand]
    private async Task EditAndResendAsync(UserMessageItem? message)
    {
        if (message is null || !HasPointBefore(message))
        {
            return;
        }
        if (CantGoBackReason() is { } reason)
        {
            _conversation.AddNote(reason, NoteKind.Warning);
            return;
        }
        var later = Items.SkipWhile(i => !ReferenceEquals(i, message)).Skip(1).OfType<UserMessageItem>().Count();
        var body = (later switch
        {
            0 => "The conversation goes back to just before this message, which goes in the composer to change and send again.",
            1 => "The conversation goes back to just before this message, leaving out the one after it. This message goes in the composer to change and send again.",
            _ => $"The conversation goes back to just before this message, leaving out the {later} after it. This message goes in the composer to change and send again.",
        }) + " The session as it was stays in History.";
        var files = await PreviewFileRestoreAsync(message);
        if (files is { FilesChanged.Count: > 0 })
        {
            _shell.Confirm(
                "Edit and resend this message?",
                $"{body} Claude changed {DescribeFiles(files.FilesChanged)} since; restoring puts {(files.FilesChanged.Count == 1 ? "it" : "them")} back as before this message.",
                "Go back and restore files",
                () => GoBackAsync(message, restoreFiles: true),
                "Go back, keep files",
                () => GoBackAsync(message, restoreFiles: false));
            return;
        }
        _shell.Confirm("Edit and resend this message?", body, "Go back", () => GoBackAsync(message, restoreFiles: false));
    }

    private async Task GoBackAsync(UserMessageItem message, bool restoreFiles)
    {
        // Things may have moved on while the confirmation was up.
        if (!HasPointBefore(message) || CantGoBackReason() is not null || IsTranscriptMissing(message.ResumeAt))
        {
            return;
        }
        var restored = false;
        if (restoreFiles && message.Uuid is { } uuid && _session is { } session)
        {
            if (!await RestoreFilesAsync(session, uuid))
            {
                return;
            }
            restored = true;
        }
        var text = message.Text;
        var images = message.Images;
        var note = restored
            ? "Went back to before your message, and put the files back as they were then. Edit it and send it again."
            : "Went back to before your message. Edit it and send it again.";
        if (!await RestartAtAsync(message.ResumeAt, dropsTurn: IsLastPrompt(message) ? message.Uuid : null, note))
        {
            return;
        }
        PutInComposer(text, images);
    }

    /// <summary>
    /// <b>Branch from here</b>: a new tab in the same folder that carries the conversation on from just before the
    /// message, with the message in its composer. This tab and its session stay as they are.
    /// </summary>
    [RelayCommand]
    private Task BranchFromHereAsync(UserMessageItem? message) =>
        message is null || !HasPointBefore(message)
            ? Task.CompletedTask
            : _shell.OpenCopyAsync(CopyState(message.ResumeAt is null ? CopyPoint.Nothing : CopyPoint.At(message.ResumeAt)), message.Text, message.Images);

    /// <summary>
    /// <b>Restore files to before this</b>: Claude Code puts the files it changed since the message back as they were,
    /// after a confirmation listing them. The conversation stays as it is.
    /// </summary>
    [RelayCommand]
    private async Task RestoreFilesBeforeAsync(UserMessageItem? message)
    {
        if (message?.Uuid is not { } uuid || !Items.Contains(message))
        {
            return;
        }
        if (CantGoBackReason() is { } reason)
        {
            _conversation.AddNote(reason, NoteKind.Warning);
            return;
        }
        if (_session is not { } session)
        {
            _conversation.AddNote("Claude Code isn't running in this tab. Restart it to put files back.", NoteKind.Warning);
            return;
        }
        RewindResult preview;
        try
        {
            preview = await session.RewindFilesAsync(uuid, dryRun: true);
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't check which files would change: {ex.Message}", NoteKind.Error);
            return;
        }
        if (!preview.CanRewind)
        {
            _conversation.AddNote(NoCheckpointText(preview), NoteKind.Warning);
            return;
        }
        if (preview.FilesChanged.Count == 0)
        {
            _conversation.AddNote("No files changed since that message.");
            return;
        }
        var counts = preview.Insertions > 0 || preview.Deletions > 0 ? $" (+{preview.Insertions} −{preview.Deletions})" : "";
        _shell.Confirm(
            "Restore files to before this message?",
            $"Claude Code puts {DescribeFiles(preview.FilesChanged)}{counts} back as {(preview.FilesChanged.Count == 1 ? "it was" : "they were")} before it. "
            + "Later changes to them, Claude's or yours, are lost. The conversation stays as it is.",
            "Restore files",
            async () =>
            {
                if (_session is { } current && await RestoreFilesAsync(current, uuid))
                {
                    _conversation.AddNote($"Put {DescribeFiles(preview.FilesChanged)} back as before that message.");
                    _ = ChangedFiles.RefreshAsync();
                }
            });
    }

    /// <summary>A dry run of <c>rewind_files</c>, or null when there's no session, no id or no checkpoint to restore.</summary>
    private async Task<RewindResult?> PreviewFileRestoreAsync(UserMessageItem message)
    {
        if (message.Uuid is not { } uuid || _session is not { } session)
        {
            return null;
        }
        try
        {
            var preview = await session.RewindFilesAsync(uuid, dryRun: true);
            return preview.CanRewind ? preview : null;
        }
        catch (Exception)
        {
            // Claude Code may not keep checkpoints (Settings → Claude Code, or an older version): go back without them.
            return null;
        }
    }

    private async Task<bool> RestoreFilesAsync(ClaudeSession session, string uuid)
    {
        try
        {
            var result = await session.RewindFilesAsync(uuid);
            if (!result.CanRewind)
            {
                _conversation.AddNote(NoCheckpointText(result), NoteKind.Error);
                return false;
            }
            if (result.SkippedLinks > 0)
            {
                _conversation.AddNote($"Claude Code left {result.SkippedLinks} linked file{(result.SkippedLinks == 1 ? "" : "s")} as {(result.SkippedLinks == 1 ? "it was" : "they were")}.", NoteKind.Warning);
            }
            return true;
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't put the files back: {ex.Message}", NoteKind.Error);
            return false;
        }
    }

    private static string NoCheckpointText(RewindResult result) =>
        $"Claude Code can't put files back to before that message: {result.Error ?? "it has no copies of them from then"}."
        + " It keeps them while Settings → Claude Code → Keep copies of files Claude changes is on.";

    /// <summary>"Program.cs", "Program.cs and App.cs", or "5 files".</summary>
    internal static string DescribeFiles(IReadOnlyList<string> files) => files.Count switch
    {
        1 => Path.GetFileName(files[0]),
        2 => $"{Path.GetFileName(files[0])} and {Path.GetFileName(files[1])}",
        _ => string.Create(CultureInfo.InvariantCulture, $"{files.Count} files"),
    };

    /// <summary>
    /// Stops this tab's Claude Code and starts it again at <paramref name="resumeAt"/>, as a copy of the session, with
    /// the conversation read again up to that point. With no point, a new session starts: there was nothing before.
    /// </summary>
    /// <param name="dropsTurn">The prompt the resume leaves out, when it's the last: Claude Code checks nothing else goes.</param>
    private async Task<bool> RestartAtAsync(string? resumeAt, string? dropsTurn, string note)
    {
        var sessionId = State.SessionId;
        if (IsTranscriptMissing(resumeAt))
        {
            return false;
        }
        if (!State.ForkOnNextStart && !_forkAwaitingId)
        {
            // This tab no longer carries the session on: another machine may.
            ReleaseLease();
        }
        await StopSessionAsync();
        _conversation.Clear();
        ChangedFiles.Reset();
        Status = TabStatus.NotStarted;
        if (resumeAt is null || sessionId is null)
        {
            State.SessionId = null;
            State.TranscriptPath = null;
            State.SessionStartedAt = null;
            State.ForkOnNextStart = false;
            State.ResumeAt = null;
            State.ResumeDropsTurn = null;
            State.Plan = null;
            _forkAwaitingId = false;
            _restoredTranscript = true;
            _conversation.AddNote(note);
        }
        else
        {
            State.ForkOnNextStart = true;
            State.ResumeAt = resumeAt;
            State.ResumeDropsTurn = dropsTurn;
            // Read again, up to the point.
            _restoredTranscript = false;
            _forkNote = note;
        }
        _services.SaveState();
        await EnsureStartedAsync();
        return true;
    }

    /// <summary>Going back to <paramref name="resumeAt"/> needs the transcript to resume from: says so when it's gone.</summary>
    private bool IsTranscriptMissing(string? resumeAt)
    {
        if (resumeAt is null || State.SessionId is not { } sessionId || _services.Library.FindTranscript(sessionId, State.TranscriptPath) is not null)
        {
            return false;
        }
        _conversation.AddNote("Couldn't find this session's transcript to go back in.", NoteKind.Error);
        return true;
    }

    /// <summary>Puts a message back in the composer, its images attached again.</summary>
    internal void PutInComposer(string text, IReadOnlyList<MessageImage> images)
    {
        ComposerText = text;
        Attachments.Clear();
        PastedTexts.Clear();
        for (var i = 0; i < images.Count; i++)
        {
            TryAttach(images[i], string.Create(CultureInfo.InvariantCulture, $"Image {i + 1}"));
        }
        AttachmentError = null;
        SendCommand.NotifyCanExecuteChanged();
        ComposerFocusRequested?.Invoke();
    }

    /// <summary>
    /// The state for a new tab copying this one (DESIGN.md §5, "Rewind and branch"): same folder, settings and kept
    /// suffixes, carrying the session on as a copy up to <paramref name="point"/>.
    /// </summary>
    internal TabState CopyState(CopyPoint point)
    {
        var sessionId = point.Keeps ? State.SessionId : null;
        // A copy that hasn't had its first turn is still the original up to its own point: the whole of it ends there.
        var (resumeAt, dropsTurn) = point.ResumeAt is null && State.ForkOnNextStart ? (State.ResumeAt, State.ResumeDropsTurn) : (point.ResumeAt, null);
        return new TabState
        {
            Folder = State.Folder,
            SessionId = sessionId,
            TranscriptPath = sessionId is null ? null : State.TranscriptPath,
            ForkOnNextStart = sessionId is not null,
            ResumeAt = sessionId is null ? null : resumeAt,
            ResumeDropsTurn = sessionId is null ? null : dropsTurn,
            AutoName = State.AutoName,
            SyncToLibrary = State.SyncToLibrary,
            RemoteControl = _services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp,
            Overrides = Copy(State.Overrides) ?? new TabOverrides(),
            KeptSuffixes = [.. State.KeptSuffixes],
            ExpandThinking = State.ExpandThinking,
            // A copy has the same changes, so keeps their marks (DESIGN.md §8, "Reviewed").
            ReviewedFiles = sessionId is null ? [] : Copy(State.ReviewedFiles) ?? [],
            AllFilesReviewed = sessionId is not null && State.AllFilesReviewed,
            // In the same worktree, with the same extra folders (DESIGN.md §4, "Worktree tabs").
            WorktreeOf = State.WorktreeOf,
            NewWorktree = State.NewWorktree,
            ExtraFolders = [.. State.ExtraFolders],
        };
    }
}

/// <summary>How much of a session a new tab copying it keeps.</summary>
internal readonly record struct CopyPoint(bool Keeps, string? ResumeAt)
{
    /// <summary>All of it: <b>Duplicate tab</b>.</summary>
    public static CopyPoint Whole => new(true, null);

    /// <summary>Up to and including an entry: <b>Branch from here</b>.</summary>
    public static CopyPoint At(string resumeAt) => new(true, resumeAt);

    /// <summary>None of it: branching from the first message starts a new session.</summary>
    public static CopyPoint Nothing => new(false, null);
}
