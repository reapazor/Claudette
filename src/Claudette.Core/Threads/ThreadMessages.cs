using System.Text;

namespace Claudette.Core.Threads;

/// <summary>How a sub-thread's work for its thread ended (DESIGN.md §18, "Threads").</summary>
public enum SubThreadOutcome
{
    Finished,
    Stopped,
    Failed,
    Closed,
    Detached,
}

/// <summary>One sub-thread's part of the report a thread gets once its sub-threads have finished.</summary>
/// <param name="Name">The sub-thread's name, as the thread knows it.</param>
/// <param name="Reply">Its last reply, if it gave one.</param>
/// <param name="Error">Why it failed, for <see cref="SubThreadOutcome.Failed"/>.</param>
public sealed record SubThreadReport(string Name, SubThreadOutcome Outcome, string? Reply, string? Error = null);

/// <summary>
/// What Claudette tells Claude about threads (DESIGN.md §18, "Threads"): the note on a thread's next message, the
/// answers its <c>SendMessage</c> calls get, the message a sub-thread receives and the report that comes back.
/// </summary>
public static class ThreadMessages
{
    /// <summary>How long a reply in a report can be: the rest is in the sub-thread's tab.</summary>
    public const int MaxReplyLength = 20_000;

    /// <summary>How long a send waits for the user's answer when the thread asks first.</summary>
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromMinutes(10);

    /// <summary>The note on a thread's next message, after it became one or its sub-threads changed.</summary>
    public static string Note(IReadOnlyList<string> subThreads)
    {
        if (subThreads.Count == 0)
        {
            return "[Claudette] This tab is now a thread, which can hand work to Claude Code sessions in other tabs: its sub-threads. "
                + "It has none yet; the user assigns them.";
        }
        var names = string.Join(", ", subThreads.Select(n => $"\"{n}\""));
        return $"[Claudette] This tab is a thread. Its sub-threads are other tabs, each a Claude Code session of its own: {names}. "
            + "To give one work, call SendMessage with its name as `to`. Claudette delivers it, as that tab's next turn if it's busy. "
            + "When every sub-thread you messaged has finished, Claudette sends you their results in one message, "
            + "so don't wait, poll or ask to be told: end your turn once you've handed out the work. "
            + "The user can see and steer each sub-thread, and answers its permission prompts and questions.";
    }

    /// <summary>The note on a former thread's next message.</summary>
    public const string NoLongerAThread =
        "[Claudette] This tab is no longer a thread: it has no sub-threads, and SendMessage reaches other sessions as usual.";

    /// <summary>
    /// The answer to a <c>SendMessage</c> Claudette delivered itself. Claude Code shows the hook's answer as a refusal,
    /// so it says plainly that the message went.
    /// </summary>
    /// <param name="waits">The sub-thread is busy, so the message waits for its turn to end.</param>
    public static string Delivered(string name, bool waits) =>
        (waits
            ? $"Claudette delivered this message to the sub-thread \"{name}\". It's busy, so the message waits there and starts its next turn once the current one ends."
            : $"Claudette delivered this message to the sub-thread \"{name}\", where it starts the next turn.")
        + " It went, although this reads as an error: don't send it again. When every sub-thread you've messaged has finished, "
        + "Claudette sends you their results in one message, so there's no need to wait, poll or ask to be told.";

    /// <summary>The answer to a call that only asked to be told when a sub-thread is idle.</summary>
    public static string NoSubscriptionNeeded(string name) =>
        $"Nothing was sent. \"{name}\" is one of this thread's sub-threads: Claudette sends you its result when it finishes the work you gave it, "
        + "so there's no need to ask to be told.";

    /// <summary>The answer when the user didn't let the message go.</summary>
    public static string Declined(string name) =>
        $"Not sent: the user chose not to send this message to \"{name}\". Don't send it again unless they ask you to.";

    /// <summary>The answer when the user didn't say within <see cref="ApprovalWindow"/>.</summary>
    public static string NotAnswered(string name) =>
        $"Not sent: the user didn't say whether to send this message to \"{name}\" within {ApprovalWindow.TotalMinutes:0} minutes.";

    /// <summary>The answer when the sub-thread left the thread, or closed, while the user was asked.</summary>
    public static string Gone(string name) => $"Not sent: \"{name}\" is no longer one of this thread's sub-threads.";

    /// <summary>
    /// Whether a <c>SendMessage</c> result is Claudette's answer for a message that went, or that didn't need to: the
    /// thread's tool row shows it as done, not failed.
    /// </summary>
    public static bool IsDeliveryAnswer(string? text) =>
        text is not null && (text.StartsWith("Claudette delivered this message to the sub-thread", StringComparison.Ordinal)
            || text.StartsWith("Nothing was sent. ", StringComparison.Ordinal));

    /// <summary>What a sub-thread receives: the thread's message, saying who it's from and where the answer goes.</summary>
    public static string ForSubThread(string thread, string message) =>
        $"Message from the thread \"{thread}\", the tab coordinating this work. When you finish, Claudette gives it your final reply, "
        + $"so answer here rather than messaging it.\n\n{message}";

    /// <summary>The report a thread gets once every sub-thread it messaged has finished: their outcomes and last replies.</summary>
    public static string Report(IReadOnlyList<SubThreadReport> reports)
    {
        var text = new StringBuilder(reports.Count == 1
            ? "[Claudette] Your sub-thread has finished the work you sent it."
            : "[Claudette] Your sub-threads have finished the work you sent them.");
        foreach (var report in reports)
        {
            text.Append("\n\n## ").Append(report.Name).Append("\n\n");
            text.Append(report.Outcome switch
            {
                SubThreadOutcome.Finished => "Finished.",
                SubThreadOutcome.Stopped => "Stopped by the user before it finished.",
                SubThreadOutcome.Failed => report.Error is { Length: > 0 } error ? $"Ended with an error: {error}" : "Ended with an error.",
                SubThreadOutcome.Closed => "Its tab was closed before it finished.",
                _ => "It was taken off this thread before it finished.",
            });
            if (report.Reply is { Length: > 0 } reply)
            {
                text.Append(report.Outcome == SubThreadOutcome.Finished ? " Its final reply:\n\n" : " Its last reply:\n\n");
                text.Append(reply.Length > MaxReplyLength
                    ? $"{reply[..MaxReplyLength]}\n\n(Cut short here; the rest is in its tab.)"
                    : reply);
            }
            else if (report.Outcome == SubThreadOutcome.Finished)
            {
                text.Append(" It gave no reply.");
            }
        }
        return text.ToString();
    }
}
