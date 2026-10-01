using System.Text;
using Claudette.Core.Files;

namespace Claudette.Core.Tests.Files;

public sealed class Utf8LinesTests
{
    private static Utf8Line[] Read(byte[] bytes, long start = 0) => [.. Utf8Lines.Read(new MemoryStream(bytes), start, TestContext.Current.CancellationToken)];

    [Fact]
    public void Lines_come_with_where_the_next_one_starts()
    {
        var bytes = Encoding.UTF8.GetBytes("one\r\ntwö\nthree");

        Assert.Equal([new Utf8Line("one", 5), new Utf8Line("twö", 10), new Utf8Line("three", -1)], Read(bytes));
        // Reading on from an end gives the rest.
        Assert.Equal([new Utf8Line("twö", 10), new Utf8Line("three", -1)], Read(bytes, 5));
    }

    [Fact]
    public void A_byte_order_mark_is_skipped_but_counted()
    {
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("a\nb\n")];

        Assert.Equal([new Utf8Line("a", 5), new Utf8Line("b", 7)], Read(bytes));
    }

    [Fact]
    public void Lines_longer_than_the_buffer_and_characters_across_it_read_whole()
    {
        // The é's two bytes straddle the 64 KB read.
        var long1 = new string('x', 64 * 1024 - 1) + "é" + new string('y', 70_000);
        var bytes = Encoding.UTF8.GetBytes(long1 + "\nshort\n");

        var lines = Read(bytes);

        Assert.Equal(long1, lines[0].Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(long1) + 1, lines[0].End);
        Assert.Equal(new Utf8Line("short", bytes.Length), lines[1]);
    }
}
