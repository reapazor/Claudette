using Claudette.Core.Composer;

namespace Claudette.Core.Tests.Composer;

/// <summary>Large pastes kept as attachments (DESIGN.md §5, "Attachments").</summary>
public sealed class PastedTextTests
{
    [Fact]
    public void Text_over_32_KB_in_UTF8_is_large()
    {
        Assert.False(PastedText.IsLarge(null));
        Assert.False(PastedText.IsLarge(new string('a', PastedText.LargeBytes)));
        Assert.True(PastedText.IsLarge(new string('a', PastedText.LargeBytes + 1)));
        // Counted in UTF-8: fewer characters, each of several bytes.
        Assert.True(PastedText.IsLarge(new string('é', PastedText.LargeBytes / 2 + 1)));
        Assert.False(PastedText.IsLarge(new string('é', PastedText.LargeBytes / 2)));
    }

    [Fact]
    public void It_says_how_many_lines_and_kilobytes()
    {
        var log = string.Join("\n", Enumerable.Range(1, 1204).Select(i => $"line {i} " + new string('x', 30))) + "\n";

        Assert.Equal($"Pasted text · {1204:N0} lines · 48 KB", PastedText.Describe(log));
        Assert.Equal("Pasted text · 1 line · 1 KB", PastedText.Describe("one"));
    }

    [Fact]
    public void Its_tooltip_shows_the_first_lines()
    {
        var text = string.Join("\r\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));

        var preview = PastedText.Preview(text);

        Assert.Equal(string.Join("\n", Enumerable.Range(1, PastedText.PreviewLines).Select(i => $"line {i}")) + "\n…", preview);
        Assert.Equal("short", PastedText.Preview("short"));
    }

    [Fact]
    public void Pastes_go_after_what_was_typed_each_after_a_blank_line()
    {
        Assert.Equal("look at this\n\nlog one\n\nlog two", PastedText.Join("look at this", ["log one\n", "log two"]));
        Assert.Equal("only the log", PastedText.Join("", ["only the log\r\n"]));
        Assert.Equal("typed", PastedText.Join("typed", []));
    }
}
