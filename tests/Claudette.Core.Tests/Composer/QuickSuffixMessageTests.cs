using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Tests.Composer;

/// <summary>How quick suffixes go into the message Claude Code gets (DESIGN.md §5, "Quick suffixes").</summary>
public sealed class QuickSuffixMessageTests
{
    private const string Suffix = "Ask clarifying questions before you start.";

    [Fact]
    public void A_suffix_follows_the_text_after_a_blank_line()
    {
        var message = OutgoingMessages.UserMessage("Fix the login bug", [], Suffix);

        Assert.Equal($"Fix the login bug\n\n{Suffix}", message["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void A_message_carries_its_id_and_a_typed_one_says_a_person_sent_it()
    {
        var typed = OutgoingMessages.Stamped(OutgoingMessages.UserMessage("Fix it", [], null), new MessageStamp("0b8e6c70-1d1f-4c8e-9a43-5e2f0b9c1a77", FromUser: true));
        var checkIn = OutgoingMessages.Stamped(OutgoingMessages.UserText("Status?"), new MessageStamp("5f1c2a9e-3b7d-4e60-8d21-9c4b7a6e0f13", FromUser: false));

        Assert.Equal("""{"type":"user","message":{"role":"user","content":"Fix it"},"parent_tool_use_id":null,"session_id":"","uuid":"0b8e6c70-1d1f-4c8e-9a43-5e2f0b9c1a77","origin":{"kind":"human"}}""", typed.ToJsonString());
        Assert.Equal("5f1c2a9e-3b7d-4e60-8d21-9c4b7a6e0f13", checkIn["uuid"]!.GetValue<string>());
        Assert.False(checkIn.ContainsKey("origin"));
        Assert.False(OutgoingMessages.Stamped(OutgoingMessages.UserText("x"), null).ContainsKey("uuid"));
    }

    [Fact]
    public void A_suffix_can_be_the_whole_message()
    {
        var message = OutgoingMessages.UserMessage("", [], Suffix);

        Assert.Equal(Suffix, message["message"]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/review")]
    [InlineData("/review 42")]
    [InlineData("/compact")]
    public void After_a_slash_command_the_suffix_goes_in_its_own_block_before_it(string command)
    {
        var message = OutgoingMessages.UserMessage(command, [], Suffix);

        // Claude Code takes everything after the command's name as its arguments, and reads the command from the last block.
        Assert.Equal([Suffix, command], Texts(message));
    }

    [Fact]
    public void With_images_the_suffix_goes_between_them_and_the_command()
    {
        var message = OutgoingMessages.UserMessage("/review", [new MessageImage("image/png", AttachmentTests.Png)], Suffix);

        var content = message["message"]!["content"]!.AsArray();
        Assert.Equal(["image", "text", "text"], content.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal([Suffix, "/review"], Texts(message));
    }

    [Fact]
    public void With_images_and_plain_text_the_suffix_still_follows_the_text()
    {
        var message = OutgoingMessages.UserMessage("what is this?", [new MessageImage("image/png", AttachmentTests.Png)], Suffix);

        Assert.Equal([$"what is this?\n\n{Suffix}"], Texts(message));
    }

    [Fact]
    public void Without_a_suffix_a_slash_command_is_sent_as_it_is()
    {
        Assert.Equal("/compact", OutgoingMessages.UserMessage("/compact", [], null)["message"]!["content"]!.GetValue<string>());
        Assert.Equal("/compact", OutgoingMessages.UserMessage("/compact", [], "")["message"]!["content"]!.GetValue<string>());
    }

    private static string[] Texts(JsonObject message) =>
        [.. message["message"]!["content"]!.AsArray().Where(b => b!["type"]!.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>())];
}
