using System.Text;
using Claudette.Core.Files;

namespace Claudette.Core.Tests.Files;

public sealed class TextFileEncodingTests
{
    public static TheoryData<byte[], string, bool> Marks => new()
    {
        { [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'], "hi", true },
        { [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0], "hi", true },
        { [0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i'], "hi", true },
        { [0xFF, 0xFE, 0, 0, (byte)'h', 0, 0, 0], "h", true },
        { [(byte)'h', (byte)'i'], "hi", false },
        { [], "", false },
    };

    [Theory]
    [MemberData(nameof(Marks))]
    public void Reads_text_by_its_mark_and_writes_it_back_the_same(byte[] bytes, string text, bool hasMark)
    {
        Assert.True(TextFileEncoding.TryRead(bytes, out var encoding, out var read));

        Assert.Equal(text, read);
        Assert.Equal(hasMark, encoding.HasMark);
        Assert.Equal(bytes, encoding.GetBytes(read));
    }

    [Fact]
    public void Text_that_isnt_valid_in_its_encoding_isnt_read()
    {
        Assert.False(TextFileEncoding.TryRead([(byte)'c', 0xE9], out _, out _));
        Assert.False(TextFileEncoding.TryRead([0xEF, 0xBB, 0xBF, 0xC3], out _, out _));
    }

    [Fact]
    public void Unmarked_text_is_written_as_utf8_without_a_mark()
    {
        Assert.True(TextFileEncoding.TryRead(Encoding.UTF8.GetBytes("é"), out var encoding, out _));

        Assert.Equal([0xC3, 0xA9], encoding.GetBytes("é"));
    }
}
