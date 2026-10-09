using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Threads;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// A message a thread's Claude wants to send to a sub-thread, waiting for the user to let it go (DESIGN.md §18,
/// "Threads": <b>Ask before sending to sub-threads</b>).
/// </summary>
public sealed partial class ThreadSendApproval(string name, string message)
{
    public string Title { get; } = $"Send this to {name}?";

    public string Message { get; } = message;

    /// <summary>True to send, false not to.</summary>
    internal TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [RelayCommand]
    private void Send() => Answer.TrySetResult(true);

    [RelayCommand]
    private void DontSend() => Answer.TrySetResult(false);
}

/// <summary>A sub-thread waiting on the user, in its thread's strip over the composer (DESIGN.md §18, "Threads").</summary>
/// <param name="permission">Its waiting permission prompt, which <b>Allow</b> and <b>Deny</b> answer from here; null for anything else.</param>
public sealed partial class WaitingSubThread(TabViewModel subThread, string text, PermissionItem? permission)
{
    public TabViewModel SubThread => subThread;

    public string Name => subThread.DisplayName;

    /// <summary>What it asks, as its notification says it: "Allow this command? npm test".</summary>
    public string Text => text;

    public bool CanAnswer => permission is not null;

    [RelayCommand]
    private void Allow() => permission?.AllowCommand.Execute(null);

    [RelayCommand]
    private void Deny() => permission?.DenyCommand.Execute(null);
}

/// <summary>One of the group's threads, in a tab's <b>Assign to thread</b> submenu.</summary>
public sealed record ThreadMenuItem(TabViewModel Thread, IRelayCommand<TabViewModel> Assign)
{
    public string Name => Thread.DisplayName;
}

/// <summary>
/// Threads (DESIGN.md §18): a tab whose Claude hands work to other tabs in its group, its sub-threads, through Claude
/// Code's own <c>SendMessage</c>. A thread's hook delivers the messages, asks first when it should, and sends one report
/// once the sub-threads it messaged have finished; a sub-thread takes the messages and reports how its turns ended. The
/// shell keeps who belongs to which thread (<see cref="ShellViewModel"/>, <c>ShellViewModel.Threads.cs</c>).
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>How long Claude Code waits for the threads' hook: past the approval window, so Claudette always answers first.</summary>
    internal static readonly TimeSpan ThreadHookTimeout = ThreadMessages.ApprovalWindow + TimeSpan.FromMinutes(1);

    // ---- Who belongs where ----------------------------------------------------------------------------------------

    /// <summary>The tab is a thread: its sub-threads are other tabs in its group.</summary>
    public bool IsThread => State.IsThread;

    /// <summary>The thread this tab is a sub-thread of, or null.</summary>
    public TabViewModel? ThreadHead { get; private set; }

    public bool IsSubThread => ThreadHead is not null;

    /// <summary>A thread's sub-threads, in the order they joined it, which decides their names when two share one.</summary>
    public ObservableCollection<TabViewModel> SubThreads { get; } = [];

    /// <summary>Neither a thread nor a sub-thread: it can become a thread. Threads don't nest.</summary>
    public bool CanMakeThread => !IsThread && !IsSubThread;

    /// <summary>The thread asks the user before its Claude's message goes to a sub-thread.</summary>
    public bool AskBeforeSendingToSubThreads => State.AskBeforeSendingToSubThreads;

    /// <summary>The names the thread's Claude knows its sub-threads by, in <see cref="SubThreads"/>' order.</summary>
    internal IReadOnlyList<string> SubThreadNames => ThreadNames.Unique([.. SubThreads.Select(s => s.DisplayName)]);

    /// <summary>The group's threads this tab could join, for the menu.</summary>
    public IReadOnlyList<ThreadMenuItem> AssignableThreads => [.. _shell.ThreadsFor(this).Select(t => new ThreadMenuItem(t, AssignToThreadCommand))];

    public bool CanBeAssigned => _shell.ThreadsFor(this).Count > 0;

    /// <summary>The thread icon's tip on its row.</summary>
    public string ThreadTip => SubThreads.Count switch
    {
        0 => "A thread, with no sub-threads yet",
        1 => "A thread, with 1 sub-thread",
        var n => $"A thread, with {n} sub-threads",
    };

    /// <summary>On a thread's row while its sub-threads work, when nothing else needs saying: "Thread · 2 of 3 working".</summary>
    private string? ThreadRowDetail => IsThread && SubThreads.Count(s => s.IsWorking) is > 0 and var working
        ? $"Thread · {working} of {SubThreads.Count} working"
        : null;

    /// <summary>Called by the shell as the tab becomes a thread or stops being one.</summary>
    internal void SetIsThread(bool on)
    {
        State.IsThread = on;
        if (!on)
        {
            foreach (var approval in ThreadApprovals.ToArray())
            {
                approval.Answer.TrySetResult(false);
            }
        }
        ThreadRoleChanged();
    }

    /// <summary>Called by the shell as the tab joins a thread or leaves it.</summary>
    internal void SetThreadHead(TabViewModel? head)
    {
        ThreadHead = head;
        State.ThreadId = head?.Id;
        _threadReply = null;
        ThreadRoleChanged();
    }

    private void ThreadRoleChanged()
    {
        OnPropertyChanged(nameof(IsThread));
        OnPropertyChanged(nameof(ThreadHead));
        OnPropertyChanged(nameof(IsSubThread));
        OnPropertyChanged(nameof(CanMakeThread));
        OnPropertyChanged(nameof(AskBeforeSendingToSubThreads));
        SubThreadsChanged();
        _services.SaveState();
    }

    /// <summary>The group's threads changed: the <b>Assign to thread</b> submenu follows.</summary>
    internal void ThreadMenuChanged()
    {
        OnPropertyChanged(nameof(AssignableThreads));
        OnPropertyChanged(nameof(CanBeAssigned));
    }

    [RelayCommand]
    private void AssignToThread(TabViewModel? head)
    {
        if (head is not null)
        {
            _shell.AssignToThread(this, head);
        }
    }

    [RelayCommand]
    private void ToggleAskBeforeSendingToSubThreads()
    {
        State.AskBeforeSendingToSubThreads = !State.AskBeforeSendingToSubThreads;
        OnPropertyChanged(nameof(AskBeforeSendingToSubThreads));
        _services.SaveState();
    }

    /// <summary>A sub-thread joins this thread.</summary>
    internal void AttachSubThread(TabViewModel subThread)
    {
        SubThreads.Add(subThread);
        subThread.PropertyChanged += OnSubThreadChanged;
        SubThreadsChanged();
    }

    /// <summary>A sub-thread leaves this thread; if the thread waited on it, its report says how.</summary>
    internal void DetachSubThread(TabViewModel subThread, SubThreadOutcome outcome)
    {
        var name = NameOf(subThread);
        subThread.PropertyChanged -= OnSubThreadChanged;
        SubThreads.Remove(subThread);
        if (name is not null)
        {
            OnSubThreadFinished(subThread, new SubThreadReport(name, outcome, null));
        }
        SubThreadsChanged();
    }

    /// <summary>The name a sub-thread goes by in this thread; null for a tab that isn't one of its sub-threads.</summary>
    internal string? NameOf(TabViewModel subThread) =>
        SubThreads.IndexOf(subThread) is >= 0 and var index ? SubThreadNames[index] : null;

    private void OnSubThreadChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Status) or nameof(DisplayName))
        {
            SubThreadsChanged();
        }
    }

    private void SubThreadsChanged()
    {
        OnPropertyChanged(nameof(WaitingSubThreads));
        OnPropertyChanged(nameof(HasThreadStrip));
        OnPropertyChanged(nameof(ThreadTip));
        OnPropertyChanged(nameof(RowDetail));
    }

    // ---- A thread's hook on SendMessage ---------------------------------------------------------------------------

    /// <summary>Messages the thread's Claude wants to send, waiting for the user (<see cref="AskBeforeSendingToSubThreads"/>).</summary>
    public ObservableCollection<ThreadSendApproval> ThreadApprovals { get; } = [];

    /// <summary>The sub-threads this thread waits on: it messaged them, and they haven't finished.</summary>
    private readonly HashSet<TabViewModel> _awaitedSubThreads = [];

    /// <summary>What the sub-threads it waited on said, for the report.</summary>
    private readonly List<SubThreadReport> _subThreadReports = [];

    /// <summary>The PreToolUse hook every tab registers on <c>SendMessage</c>, with a timeout past the approval window.</summary>
    private HookRegistration ThreadHook() => new("PreToolUse", SendMessageCall.ToolName, OnSendMessageAsync, ThreadHookTimeout);

    /// <summary>
    /// Claude calls <c>SendMessage</c>. For one of this thread's sub-threads, Claudette delivers the message itself,
    /// after asking when it should, and stops Claude Code's own delivery; anything else goes on as it would. Called off
    /// the UI thread.
    /// </summary>
    private async Task<JsonObject> OnSendMessageAsync(HookInput input, CancellationToken cancellationToken)
    {
        if (SendMessageCall.From(input) is not { } call)
        {
            return HookOutputs.Continue();
        }
        var found = await OnUiThreadAsync(() => FindSubThread(call.To));
        if (found is not var (subThread, name))
        {
            return HookOutputs.Continue();
        }
        if (call.Message.Trim().Length == 0)
        {
            return HookOutputs.Deny(ThreadMessages.NoSubscriptionNeeded(name));
        }
        if (await OnUiThreadAsync(() => AskBeforeSendingToSubThreads))
        {
            var approval = await OnUiThreadAsync(() => ShowApproval(name, call.Message));
            bool? send;
            try
            {
                send = await approval.Answer.Task.WaitAsync(ThreadMessages.ApprovalWindow, _services.Time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                send = null;
            }
            finally
            {
                _services.Dispatcher.Post(() => ResolveApproval(approval));
            }
            if (send != true)
            {
                return HookOutputs.Deny(send is null ? ThreadMessages.NotAnswered(name) : ThreadMessages.Declined(name));
            }
        }
        var waits = await OnUiThreadAsync(() => SubThreads.Contains(subThread) ? Deliver(subThread, call.Message) : (bool?)null);
        return HookOutputs.Deny(waits is { } busy ? ThreadMessages.Delivered(name, busy) : ThreadMessages.Gone(name));
    }

    /// <summary>The sub-thread <paramref name="recipient"/> names, and its name; null when this isn't a thread or none does.</summary>
    private (TabViewModel SubThread, string Name)? FindSubThread(string recipient)
    {
        if (!IsThread)
        {
            return null;
        }
        var names = SubThreadNames;
        return ThreadNames.Find(names, recipient) is { } index ? (SubThreads[index], names[index]) : null;
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread, where the tabs are, and gives back its result.</summary>
    private Task<T> OnUiThreadAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Dispatcher.Post(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>The card over the composer: the tab waits on the user, as for a permission prompt.</summary>
    private ThreadSendApproval ShowApproval(string name, string message)
    {
        var approval = new ThreadSendApproval(name, message);
        ThreadApprovals.Add(approval);
        OnPropertyChanged(nameof(HasThreadStrip));
        _waitingOnUser.Add(approval);
        _checkIns.SetWaitingOnUser(true);
        UpdateStatus();
        var body = $"Claude wants to send a message to {name}.";
        _services.Notifications.Notify(NotificationKind.NeedsInput, DisplayName, body, Id);
        _shell.Announce($"{DisplayName}: {body}");
        return approval;
    }

    private void ResolveApproval(ThreadSendApproval approval)
    {
        ThreadApprovals.Remove(approval);
        OnPropertyChanged(nameof(HasThreadStrip));
        PermissionResolved(approval);
    }

    /// <summary>Hands the message to <paramref name="subThread"/>, and waits on it. Returns whether it waits for its turn to end.</summary>
    private bool Deliver(TabViewModel subThread, string message)
    {
        _awaitedSubThreads.Add(subThread);
        return subThread.ReceiveFromThread(this, message);
    }

    /// <summary>
    /// The thread closes or stops being one: it waits on none of its sub-threads any more, and gets no report, which
    /// would start a turn for nothing.
    /// </summary>
    internal void ForgetSubThreadWork()
    {
        _awaitedSubThreads.Clear();
        _subThreadReports.Clear();
    }

    /// <summary>
    /// A sub-thread finished what this thread sent it, or left, or closed. Once none is waited on any more, the thread
    /// gets one report.
    /// </summary>
    internal void OnSubThreadFinished(TabViewModel subThread, SubThreadReport report)
    {
        if (!_awaitedSubThreads.Remove(subThread))
        {
            return;
        }
        _subThreadReports.Add(report);
        if (_awaitedSubThreads.Count > 0)
        {
            return;
        }
        var text = ThreadMessages.Report(_subThreadReports);
        _subThreadReports.Clear();
        // Into the thread's turn if it's working, as a check-in goes; otherwise as its next turn.
        var stamp = NewStamp(fromUser: false);
        var card = _conversation.AddUserMessage(text, label: "From the sub-threads", isFromThread: true);
        card.SentId = stamp.Uuid;
        card.IsQueued = IsInTurn;
        _ = SendRawAsync(text, stamp: stamp);
    }

    // ---- A sub-thread's side --------------------------------------------------------------------------------------

    /// <summary>Messages from the thread handed to Claude Code since the last turn ended: the next end reports on them.</summary>
    private int _threadMessagesSent;

    /// <summary>How the sub-thread's last turn for the thread ended, while more of the thread's messages wait.</summary>
    private SubThreadReport? _threadReply;

    /// <summary>
    /// A message from <paramref name="head"/>: labeled with who it's from, and sent at once, or held until the turn
    /// ends when the tab is busy (DESIGN.md §5, "Queued messages"). Claude reads who it's from and where its reply goes.
    /// Returns whether it waits.
    /// </summary>
    internal bool ReceiveFromThread(TabViewModel head, string message)
    {
        var text = ThreadMessages.ForSubThread(head.DisplayName, message);
        var stamp = NewStamp(fromUser: false);
        var card = _conversation.AddUserMessage(message, label: $"From the thread {head.DisplayName}", isFromThread: true);
        card.SentId = stamp.Uuid;
        if (IsWorking || IsInTurn)
        {
            Hold(card, new PendingMessage(text, [], null, stamp));
            return true;
        }
        _threadMessagesSent++;
        _ = SendRawAsync(text, stamp: stamp);
        return false;
    }

    /// <summary>A held message goes to Claude Code: one from the thread counts toward the report.</summary>
    private void OnHeldMessageSent(UserMessageItem card)
    {
        if (card.IsFromThread)
        {
            _threadMessagesSent++;
        }
    }

    private bool HoldsThreadMessages => _held.Any(h => h.Card.IsFromThread);

    /// <summary>
    /// A turn ended. If it ran messages from the thread, how it ended goes to the thread, once no more of its messages
    /// wait here.
    /// </summary>
    private void OnTurnEndedForThread(ResultMessage result)
    {
        if (_threadMessagesSent == 0)
        {
            return;
        }
        _threadMessagesSent = 0;
        var name = ThreadHead?.NameOf(this) ?? DisplayName;
        _threadReply = result.TerminalReason == "aborted_streaming"
            ? new SubThreadReport(name, SubThreadOutcome.Stopped, LastReplyForThread())
            : result.IsError
                ? new SubThreadReport(name, SubThreadOutcome.Failed, null, result.Result?.Trim())
                : new SubThreadReport(name, SubThreadOutcome.Finished, result.Result?.Trim());
        if (!HoldsThreadMessages)
        {
            ReportToThread();
        }
    }

    /// <summary>
    /// The thread's work here may have ended without a turn ending it (Stop or Cancel took its messages back, or Claude
    /// Code exited): if the thread waits on this tab and nothing of its is under way or waiting here, it hears how.
    /// </summary>
    private void OnThreadWorkEnded(SubThreadOutcome outcome, string? error = null)
    {
        if (_threadMessagesSent > 0 || HoldsThreadMessages || ThreadHead is not { } head || !head._awaitedSubThreads.Contains(this))
        {
            return;
        }
        _threadReply ??= new SubThreadReport(head.NameOf(this) ?? DisplayName, outcome, LastReplyForThread(), error);
        ReportToThread();
    }

    private void ReportToThread()
    {
        if (_threadReply is { } report && ThreadHead is { } head)
        {
            head.OnSubThreadFinished(this, report);
        }
        _threadReply = null;
    }

    /// <summary>The last reply after the thread's last message, for a turn that stopped before its end.</summary>
    private string? LastReplyForThread()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            switch (Items[i])
            {
                case AssistantTextItem reply when reply.Text.Trim().Length > 0:
                    return reply.Text.Trim();
                case UserMessageItem { IsFromThread: true }:
                    return null;
            }
        }
        return null;
    }

    // ---- The strip over the composer ------------------------------------------------------------------------------

    /// <summary>The thread's sub-threads that wait on the user, each with what it asks.</summary>
    public IReadOnlyList<WaitingSubThread> WaitingSubThreads =>
        [.. SubThreads.Where(s => s.Status == TabStatus.NeedsInput).Select(s => new WaitingSubThread(s, s.WaitingText, s.WaitingPrompt as PermissionItem))];

    /// <summary>The strip shows: a message waits to be let go, or a sub-thread waits on the user.</summary>
    public bool HasThreadStrip => ThreadApprovals.Count > 0 || SubThreads.Any(s => s.Status == TabStatus.NeedsInput);

    /// <summary>What the tab waits on the user for, as its notification says it.</summary>
    internal string WaitingText => WaitingPrompt is { } prompt
        ? PromptText(prompt)
        : ThreadApprovals.FirstOrDefault() is { } approval ? approval.Title : "Waiting for you.";

    [RelayCommand]
    private void GoToSubThread(WaitingSubThread? waiting)
    {
        if (waiting is not null)
        {
            _shell.SelectTab(waiting.SubThread.Id);
        }
    }

    // ---- What the thread's Claude is told --------------------------------------------------------------------------

    /// <summary>
    /// The note on threads for the user's next message: the sub-threads, when they changed since the last one, or that
    /// the tab is no longer a thread. Null when there's nothing new to say.
    /// </summary>
    private string? TakeThreadNote()
    {
        if (IsThread)
        {
            var names = SubThreadNames;
            var roster = string.Join("\n", names);
            if (State.ThreadNote == roster)
            {
                return null;
            }
            State.ThreadNote = roster;
            _services.SaveState();
            return ThreadMessages.Note(names);
        }
        if (State.ThreadNote is null)
        {
            return null;
        }
        State.ThreadNote = null;
        _services.SaveState();
        return ThreadMessages.NoLongerAThread;
    }

    /// <summary>A message that carried a note was taken back: the next one carries it again.</summary>
    private void ThreadNoteTakenBack() => State.ThreadNote = IsThread ? null : "";
}
