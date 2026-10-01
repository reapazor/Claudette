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
}
