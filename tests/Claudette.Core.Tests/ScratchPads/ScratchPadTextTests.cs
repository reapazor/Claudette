using Claudette.Core.ScratchPads;

namespace Claudette.Core.Tests.ScratchPads;

/// <summary>Adding to a scratch pad, and keeping both machines' changes (DESIGN.md §18, "Scratch pad").</summary>
public sealed class ScratchPadTextTests
{
    [Fact]
    public void Text_added_to_an_empty_pad_starts_it()
    {
        var added = ScratchPadText.Append("", "Check the retry limit");

        Assert.Equal("Check the retry limit\n", added.Text);
        Assert.Equal((0, 21), (added.Start, added.Length));
    }

    [Fact]
    public void Text_is_added_after_a_blank_line_and_typing_carries_on_below_it()
    {
        var added = ScratchPadText.Append("First note\n\n\n", "Second note");

        Assert.Equal("First note\n\nSecond note\n", added.Text);
        Assert.Equal("Second note", added.Text.Substring(added.Start, added.Length));
    }

    [Fact]
    public void Blank_lines_around_the_addition_and_trailing_spaces_go_but_its_indent_and_line_breaks_stay()
    {
        var added = ScratchPadText.Append("note", "\r\n\r\n    indented()\r\n    next()   \r\n\r\n");

        Assert.Equal("note\n\n    indented()\n    next()\n", added.Text);
    }

    [Fact]
    public void Code_is_fenced_in_its_language()
    {
        Assert.Equal("```csharp\nvar x = 1;\n```", ScratchPadText.Fence("var x = 1;\r\n", "csharp"));
        Assert.Equal("```\nplain\n```", ScratchPadText.Fence("plain", null));
    }

    [Fact]
    public void Code_with_a_fence_of_its_own_gets_a_longer_one()
    {
        Assert.Equal("````md\n```js\nx\n```\n````", ScratchPadText.Fence("```js\nx\n```", "md"));
    }

    [Fact]
    public void Keeping_both_keeps_what_they_share_once_and_each_machines_additions()
    {
        const string shared = "# Notes\n- one\n";

        Assert.Equal("# Notes\n- one\n- mine\n- theirs\n", ScratchPadText.KeepBoth(shared + "- mine\n", shared + "- theirs\n"));
        Assert.Equal("top mine\ntop theirs\n# Notes\n- one\n", ScratchPadText.KeepBoth("top mine\n" + shared, "top theirs\n" + shared));
    }

    [Fact]
    public void Keeping_both_of_the_same_text_or_of_one_empty_pad_loses_nothing()
    {
        Assert.Equal("same\n", ScratchPadText.KeepBoth("same\n", "same\n"));
        Assert.Equal("theirs\n", ScratchPadText.KeepBoth("", "theirs\n"));
        Assert.Equal("mine\n", ScratchPadText.KeepBoth("mine\n", "  "));
    }
}
