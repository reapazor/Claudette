using System.Buffers;
using System.Text;

namespace Claudette.Core.Files;

/// <summary>A line of a JSON Lines file, and where the next one starts.</summary>
/// <param name="End">
/// The offset just past the line's newline, where reading can carry on later; -1 for a last line with no newline, which
/// may still be being written.
/// </param>
public readonly record struct Utf8Line(string Text, long End);

/// <summary>
/// Reads UTF-8 lines from a stream with the byte offset each ends at, so a file that's only ever appended to, such as a
/// transcript, can be read again from where the last read stopped (DESIGN.md §9, "History").
/// </summary>
public static class Utf8Lines
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// The lines from <paramref name="start"/> on, without their <c>\r\n</c> or <c>\n</c>. A UTF-8 byte order mark at the
    /// start of the file is skipped.
    /// </summary>
    public static IEnumerable<Utf8Line> Read(Stream stream, long start, CancellationToken cancellationToken = default)
    {
        stream.Position = start;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var pending = new ArrayBufferWriter<byte>();
        try
        {
            var end = start;
            int read;
            while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = 0;
                if (end == start && pending.WrittenCount == 0 && start == 0 && buffer.AsSpan(0, read).StartsWith(Encoding.UTF8.Preamble))
                {
                    offset = 3;
                    end = 3;
                }
                int newline;
                while ((newline = buffer.AsSpan(offset, read - offset).IndexOf((byte)'\n')) >= 0)
                {
                    end += pending.WrittenCount + newline + 1;
                    var text = Take(pending, buffer, offset, newline);
                    offset += newline + 1;
                    yield return new Utf8Line(text, end);
                }
                pending.Write(buffer.AsSpan(offset, read - offset));
            }
            if (pending.WrittenCount > 0)
            {
                yield return new Utf8Line(Decode(pending.WrittenSpan), -1);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The line: what's pending from earlier reads, then <paramref name="count"/> bytes of the buffer.</summary>
    private static string Take(ArrayBufferWriter<byte> pending, byte[] buffer, int offset, int count)
    {
        if (pending.WrittenCount == 0)
        {
            return Decode(buffer.AsSpan(offset, count));
        }
        pending.Write(buffer.AsSpan(offset, count));
        var text = Decode(pending.WrittenSpan);
        pending.Clear();
        return text;
    }

    private static string Decode(ReadOnlySpan<byte> line) => Encoding.UTF8.GetString(line.EndsWith((byte)'\r') ? line[..^1] : line);
}
