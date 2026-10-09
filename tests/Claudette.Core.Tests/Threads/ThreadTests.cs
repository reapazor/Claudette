using System.Text.Json.Nodes;
using Claudette.Core.Sessions;
using Claudette.Core.Threads;

namespace Claudette.Core.Tests.Threads;

/// <summary>
/// What a thread's PreToolUse hook reads from a <c>SendMessage</c> call, and the names and texts Claudette gives Claude
/// about threads (DESIGN.md §18, "Threads"). The hook input is as Claude Code 2.1.284 sent it.
/// </summary>
public class ThreadTests
{
    private static HookInput SendMessage(string toolInput) => HookInput.Parse(JsonNode.Parse($$$"""
        {"subtype":"hook_callback","callback_id":"hook_1","input":{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"SendMessage","tool_input":{{{toolInput}}},"tool_use_id":"toolu_6"}}
        """)!.AsObject());

    [Fact]
    public void A_send_is_read_from_the_hook_input()
    {
        var call = SendMessageCall.From(SendMessage("""
            {"to":"probe-sub","summary":"probe","message":"Build the art page next.","type":"message","recipient":"probe-sub","content":"Build the art page next."}
            """));

        Assert.Equal(new SendMessageCall("probe-sub", "Build the art page next.", false), call);
    }

    [Fact]
    public void A_subscription_on_its_own_has_no_message()
    {
        var call = SendMessageCall.From(SendMessage("""{"to":"Art page","notify_when_idle":true}"""));

        Assert.Equal(new SendMessageCall("Art page", "", true), call);
    }

    [Fact]
    public void Another_tool_or_a_call_without_a_recipient_is_not_a_send()
    {
        var bash = HookInput.Parse(JsonNode.Parse("""
            {"subtype":"hook_callback","input":{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls"}}}
            """)!.AsObject());

        Assert.Null(SendMessageCall.From(bash));
        Assert.Null(SendMessageCall.From(SendMessage("""{"message":"hi"}""")));
        Assert.Null(SendMessageCall.From(SendMessage("""{"to":"  ","message":"hi"}""")));
    }

    [Theory]
    [InlineData("Art page", "Art page")]
    [InlineData("Art page [3fa9c1]", "Art page")]
    [InlineData("\"Art page\"", "Art page")]
    [InlineData("@Art page", "Art page")]
    [InlineData("@\"release notes\"", "release notes")]
    [InlineData("  [draft] notes  ", "[draft] notes")]
    public void A_recipient_loses_the_listings_ref_quotes_and_at_sign(string to, string recipient) =>
        Assert.Equal(recipient, SendMessageCall.Recipient(to));

    [Fact]
    public void Sub_threads_with_the_same_tab_name_get_a_number()
    {
        Assert.Equal(["Art page", "Server", "art page (2)", "Art page (3)", "Tab"], ThreadNames.Unique(["Art page", "Server", "art page", "Art page", " "]));
    }

    [Fact]
    public void A_recipient_is_found_ignoring_case()
    {
        IReadOnlyList<string> names = ["Art page", "Server"];

        Assert.Equal(1, ThreadNames.Find(names, "server"));
        Assert.Null(ThreadNames.Find(names, "work-9c"));
    }

    [Fact]
    public void The_note_names_the_sub_threads_and_how_results_come_back()
    {
        var note = ThreadMessages.Note(["Art page", "Server"]);

        Assert.Contains("\"Art page\", \"Server\"", note, StringComparison.Ordinal);
        Assert.Contains("call SendMessage with its name", note, StringComparison.Ordinal);
        Assert.Contains("end your turn", note, StringComparison.Ordinal);
        Assert.Contains("has none yet", ThreadMessages.Note([]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_gives_each_outcome_and_reply()
    {
        var report = ThreadMessages.Report(
        [
            new SubThreadReport("Art page", SubThreadOutcome.Finished, "The art page is done."),
            new SubThreadReport("Server", SubThreadOutcome.Failed, null, "Claude Code stopped unexpectedly (exit code 3)."),
            new SubThreadReport("Broadcast", SubThreadOutcome.Stopped, "Half way."),
            new SubThreadReport("Quiet", SubThreadOutcome.Finished, null),
        ]);

        Assert.Equal("""
            [Claudette] Your sub-threads have finished the work you sent them.

            ## Art page

            Finished. Its final reply:

            The art page is done.

            ## Server

            Ended with an error: Claude Code stopped unexpectedly (exit code 3).

            ## Broadcast

            Stopped by the user before it finished. Its last reply:

            Half way.

            ## Quiet

            Finished. It gave no reply.
            """.ReplaceLineEndings("\n"), report);
    }

    [Fact]
    public void A_long_reply_is_cut_short_in_the_report()
    {
        var report = ThreadMessages.Report([new SubThreadReport("Big", SubThreadOutcome.Finished, new string('x', ThreadMessages.MaxReplyLength + 10))]);

        Assert.EndsWith("(Cut short here; the rest is in its tab.)", report, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', ThreadMessages.MaxReplyLength + 1), report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_thread_is_told_who_the_message_is_from()
    {
        var text = ThreadMessages.ForSubThread("Plan the rework", "Build the art page.");

        Assert.StartsWith("Message from the thread \"Plan the rework\"", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\nBuild the art page.", text, StringComparison.Ordinal);
    }
}
