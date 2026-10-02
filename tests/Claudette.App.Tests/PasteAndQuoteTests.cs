using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.Core.Composer;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// Large pastes kept as attachments and long messages shown short (DESIGN.md §5, "Attachments"), quoting a reply into the
/// composer (DESIGN.md §5, "Copy and times"), and which key sends (DESIGN.md §14, "Keyboard shortcuts").
/// </summary>
public class PasteAndQuoteTests
{
    private static readonly string BigLog = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"log line {i:D5} with something in it")) + "\nthe-needle-at-the-end";

    [Fact]
    public async Task A_large_paste_becomes_an_attachment_and_a_small_one_pastes_as_text()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        await h.Platform.SetClipboardTextAsync("a few words");
        Assert.Null(await tab.PasteAttachmentsAsync());
        Assert.Empty(tab.PastedTexts);

        await h.Platform.SetClipboardTextAsync(BigLog);
        Assert.Equal("", await tab.PasteAttachmentsAsync());

        var paste = Assert.Single(tab.PastedTexts);
        Assert.Equal(PastedText.Describe(BigLog), paste.Label);
        Assert.StartsWith("log line 00001", paste.Preview, StringComparison.Ordinal);
        Assert.True(tab.SendCommand.CanExecute(null));
        Assert.Equal(BigLog, await tab.ClipboardTextAsync());

        tab.RemovePastedTextCommand.Execute(paste);
        Assert.Empty(tab.PastedTexts);
        Assert.False(tab.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_paste_goes_after_what_was_typed_and_the_card_shows_its_start()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "why does this fail?";
        tab.AddPastedText(BigLog);

        await tab.SendCommand.ExecuteAsync(null);

        Assert.Contains($"why does this fail?\n\n{BigLog}", h.Transport.SentUserTexts);
        Assert.Empty(tab.PastedTexts);
        var card = tab.Items.OfType<UserMessageItem>().Last();
        Assert.True(card.IsTextCut);
        Assert.Equal(UserMessageItem.ShownLineLimit, card.ShownText.Split('\n').Length);
        Assert.StartsWith("Show all (", card.ShowAllText, StringComparison.Ordinal);

        // Find opens it when the match is only in what's hidden.
        tab.Find.IsOpen = true;
        tab.Find.Query = "the-needle-at-the-end";
        await TabTestHarness.Eventually(() => card.ShowsAllText, "the whole message");
        Assert.False(card.IsTextCut);
        Assert.Equal(card.Text, card.ShownText);
    }

    [Fact]
    public void A_short_message_shows_whole()
    {
        var card = new UserMessageItem("one line");

        Assert.False(card.IsTextCut);
        Assert.Equal("one line", card.ShownText);
        Assert.False(new UserMessageItem(new string('x', UserMessageItem.ShownTextLimit)).IsTextCut);
        Assert.True(new UserMessageItem(new string('x', UserMessageItem.ShownTextLimit + 1)).IsTextCut);
    }

    [Fact]
    public async Task Quote_in_reply_puts_a_Markdown_quote_at_the_end_of_the_composer()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var focused = 0;
        tab.ComposerFocusRequested += () => focused++;

        tab.QuoteInComposer("Use exponential backoff.\n\nCap it at 30 seconds.\n");
        Assert.Equal("> Use exponential backoff.\n>\n> Cap it at 30 seconds.\n\n", tab.ComposerText);
        Assert.Equal(1, focused);

        tab.ComposerText = "Yes, and";
        tab.QuoteInComposer("one more");
        Assert.Equal("Yes, and\n\n> one more\n\n", tab.ComposerText);

        // Blank text quotes nothing.
        tab.QuoteInComposer(" \n ");
        Assert.Equal("Yes, and\n\n> one more\n\n", tab.ComposerText);

        h.Transport.EmitTurn("Here's the plan.");
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Any(r => !r.IsStreaming), "the reply");
        var reply = tab.Items.OfType<AssistantTextItem>().Last();
        tab.ComposerText = "";
        tab.QuoteMessageCommand.Execute(reply);
        Assert.Equal("> Here's the plan.\n\n", tab.ComposerText);
    }

    [Fact]
    public async Task The_composer_says_which_key_sends()
    {
        await using var h = new TabTestHarness();

        Assert.Contains("Enter to send, Shift+Enter for a new line", h.Services.Tips.ComposerPlaceholder, StringComparison.Ordinal);

        h.Services.Settings.Keyboard.SendKey = SendKey.PrimaryEnter;
        var send = new KeyChord(ChordModifiers.Primary, "Enter").Display(Claudette.App.Services.Shortcuts.IsMac);
        Assert.Contains($"{send} to send, Enter for a new line", h.Services.Tips.ComposerPlaceholder, StringComparison.Ordinal);
    }
}
