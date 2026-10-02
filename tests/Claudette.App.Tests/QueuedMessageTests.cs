using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>
/// Messages sent while Claude works (DESIGN.md §5, "Queued messages"): they wait their turn, Stop takes them back, and
/// one can be taken back on its own. The answers follow what Claude Code 2.1.286 sends.
/// </summary>
public class QueuedMessageTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static async Task<TabViewModel> WorkingAsync(TabTestHarness h, params string[] capabilities)
    {
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5",
            ["capabilities"] = new JsonArray([.. capabilities.Select(c => JsonValue.Create(c))]),
        });
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");
        return tab;
    }

    private static UserMessageItem Card(TabViewModel tab, string text) => tab.Items.OfType<UserMessageItem>().Single(m => m.Text == text);

    private static JsonObject LastRequest(TabTestHarness h, string subtype) =>
        h.Transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == subtype)["request"]!.AsObject();

    [Fact]
    public async Task A_message_sent_while_Claude_works_waits_its_turn_until_Claude_Code_takes_it()
    {
        await using var h = new TabTestHarness();
        var tab = await WorkingAsync(h);
        Assert.False(Card(tab, "long job").IsQueued);

        tab.ComposerText = "and the tests";
        await tab.SendCommand.ExecuteAsync(null);

        var queued = Card(tab, "and the tests");
        Assert.True(queued.IsQueued);
        h.Transport.Emit(new JsonObject { ["type"] = "user", ["isReplay"] = true, ["uuid"] = queued.SentId, ["message"] = new JsonObject { ["role"] = "user", ["content"] = "and the tests" } });
        await TabTestHarness.Eventually(() => !queued.IsQueued, "the echo");
        Assert.Equal(queued.SentId, queued.Uuid);
    }

    [Fact]
    public async Task Stop_takes_back_what_waits_and_puts_it_in_the_composer()
    {
        await using var h = new TabTestHarness();
        var tab = await WorkingAsync(h, "interrupt_receipt_v1", "interrupt_cancel_queued_v1");
        tab.ComposerText = "first";
        Assert.True(tab.AddImage(Png, "Pasted image"));
        await tab.SendCommand.ExecuteAsync(null);
        tab.ComposerText = "second";
        await tab.SendCommand.ExecuteAsync(null);
        var (first, second) = (Card(tab, "first"), Card(tab, "second"));
        // Claude Code cancels both, and something it ran for another reason, which isn't Claudette's.
        h.Transport.Answers["interrupt"] = _ => new JsonObject { ["still_queued"] = new JsonArray(), ["cancelled"] = new JsonArray(first.SentId, second.SentId, "scheduled-1") };
        tab.ComposerText = "typed since";

        await tab.StopCommand.ExecuteAsync(null);

        Assert.True(LastRequest(h, "interrupt")["cancel_queued"]!.GetValue<bool>());
        Assert.DoesNotContain(first, tab.Items);
        Assert.DoesNotContain(second, tab.Items);
        Assert.Contains(Card(tab, "long job"), tab.Items);
        Assert.Equal("first\n\nsecond\n\ntyped since", tab.ComposerText);
        Assert.Single(tab.Attachments);
        Assert.Equal("Took back 2 messages that were waiting their turn.", tab.Items.OfType<NoteItem>().Last().Text);
    }

    [Fact]
    public async Task Without_the_capability_Stop_leaves_them_to_run()
    {
        await using var h = new TabTestHarness();
        var tab = await WorkingAsync(h, "interrupt_receipt_v1");
        tab.ComposerText = "waiting";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Answers["interrupt"] = _ => new JsonObject { ["still_queued"] = new JsonArray(Card(tab, "waiting").SentId) };

        await tab.StopCommand.ExecuteAsync(null);

        Assert.False(LastRequest(h, "interrupt").ContainsKey("cancel_queued"));
        Assert.True(Card(tab, "waiting").IsQueued);
        Assert.Equal("", tab.ComposerText);
    }

    [Fact]
    public async Task Cancel_takes_one_back_unless_Claude_Code_has_it_already()
    {
        await using var h = new TabTestHarness();
        var tab = await WorkingAsync(h);
        tab.ComposerText = "one";
        await tab.SendCommand.ExecuteAsync(null);
        tab.ComposerText = "two";
        await tab.SendCommand.ExecuteAsync(null);
        var (one, two) = (Card(tab, "one"), Card(tab, "two"));
        h.Transport.Answers["cancel_async_message"] = request => new JsonObject { ["cancelled"] = request["message_uuid"]!.GetValue<string>() == two.SentId };

        await tab.CancelQueuedMessageCommand.ExecuteAsync(two);

        Assert.Equal(two.SentId, LastRequest(h, "cancel_async_message")["message_uuid"]!.GetValue<string>());
        Assert.DoesNotContain(two, tab.Items);
        Assert.Equal("two", tab.ComposerText);

        await tab.CancelQueuedMessageCommand.ExecuteAsync(one);
        Assert.Contains(one, tab.Items);
        Assert.True(one.IsQueued);
        Assert.StartsWith("Couldn't take that message back", tab.Items.OfType<NoteItem>().Last().Text, StringComparison.Ordinal);
    }

    // ---- Queue instead (Settings → General → Messages sent while Claude works) ----------------------------------------

    private static readonly string Result = """{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""";

    private static TabTestHarness Queueing() => new(s => s.General.MessagesWhileWorking = Core.Settings.WhileWorking.Queue);

    [Fact]
    public async Task Queued_until_the_turn_ends_each_message_goes_as_the_next_turn_one_per_turn()
    {
        await using var h = Queueing();
        var tab = await WorkingAsync(h);

        tab.ComposerText = "then the docs";
        await tab.SendCommand.ExecuteAsync(null);
        tab.ComposerText = "then the changelog";
        await tab.SendCommand.ExecuteAsync(null);

        var (docs, changelog) = (Card(tab, "then the docs"), Card(tab, "then the changelog"));
        Assert.True(docs is { IsQueued: true, IsHeld: true });
        Assert.Equal("Queued: sent when this turn ends", docs.QueuedText);
        Assert.DoesNotContain("then the docs", h.Transport.SentUserTexts);

        h.Transport.Emit(Result);
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains("then the docs"), "the first held message");
        Assert.False(docs.IsHeld);
        Assert.True(docs.IsQueued);
        Assert.DoesNotContain("then the changelog", h.Transport.SentUserTexts);
        Assert.True(changelog.IsHeld);

        // Its turn ends too: the next one goes.
        h.Transport.Emit(Result);
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains("then the changelog"), "the second held message");
    }

    [Fact]
    public async Task Send_now_sends_a_held_message_into_the_turn_and_Cancel_takes_it_back_without_asking_Claude_Code()
    {
        await using var h = Queueing();
        var tab = await WorkingAsync(h);
        tab.ComposerText = "steer after all";
        await tab.SendCommand.ExecuteAsync(null);
        tab.ComposerText = "never mind";
        await tab.SendCommand.ExecuteAsync(null);
        var (steer, never) = (Card(tab, "steer after all"), Card(tab, "never mind"));

        await tab.SendHeldNowCommand.ExecuteAsync(steer);

        Assert.Contains("steer after all", h.Transport.SentUserTexts);
        Assert.True(steer is { IsHeld: false, IsQueued: true });
        Assert.Equal("Queued: sent when Claude can take it", steer.QueuedText);

        await tab.CancelQueuedMessageCommand.ExecuteAsync(never);

        Assert.DoesNotContain(never, tab.Items);
        Assert.Equal("never mind", tab.ComposerText);
        Assert.DoesNotContain("cancel_async_message", h.Transport.SentControlSubtypes);
        h.Transport.Emit(Result);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.Working, "the turn's end");
        Assert.DoesNotContain("never mind", h.Transport.SentUserTexts);
    }

    [Fact]
    public async Task Stop_takes_back_held_messages_with_what_Claude_Code_held()
    {
        await using var h = Queueing();
        var tab = await WorkingAsync(h, "interrupt_receipt_v1", "interrupt_cancel_queued_v1");
        tab.ComposerText = "held here";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Answers["interrupt"] = _ => new JsonObject { ["still_queued"] = new JsonArray(), ["cancelled"] = new JsonArray() };

        await tab.StopCommand.ExecuteAsync(null);

        Assert.Equal("held here", tab.ComposerText);
        Assert.Equal("Took back a message that was waiting its turn.", tab.Items.OfType<NoteItem>().Last().Text);
        h.Transport.Emit(Result);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.Working, "the turn's end");
        Assert.DoesNotContain("held here", h.Transport.SentUserTexts);
    }

    [Fact]
    public async Task A_held_messages_large_pastes_come_back_as_attachments()
    {
        await using var h = Queueing();
        var tab = await WorkingAsync(h);
        var paste = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"stack frame {i:D5} at Something.Deep()"));
        tab.ComposerText = "why does this crash?";
        tab.AddPastedText(paste);
        await tab.SendCommand.ExecuteAsync(null);
        var held = Assert.Single(tab.Items.OfType<UserMessageItem>(), m => m.IsHeld);
        Assert.Equal($"why does this crash?\n\n{paste}", held.Text);

        await tab.CancelQueuedMessageCommand.ExecuteAsync(held);

        Assert.Equal("why does this crash?", tab.ComposerText);
        Assert.Equal(paste, Assert.Single(tab.PastedTexts).Text);
    }
}
