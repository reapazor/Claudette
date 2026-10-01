using System.Text;

namespace Claudette.Core.Files;

/// <summary>
/// How a text file of the user's is encoded, so that writing it back keeps it as it was: its byte order mark, if any,
/// and UTF-16 or UTF-32 when the mark says so. A file with no mark must be valid UTF-8: anything else (Latin-1, say)
/// can't be read without guessing, and writing a guess back would change characters the user never touched.
/// </summary>
public sealed class TextFileEncoding
{
    private static readonly Encoding[] Marked =
    [
        // UTF-32 LE first: its mark starts with UTF-16 LE's.
        new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
        new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
    ];

    private static readonly UTF8Encoding Unmarked = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private TextFileEncoding(Encoding encoding, bool hasMark)
    {
        Encoding = encoding;
        HasMark = hasMark;
    }

    public Encoding Encoding { get; }

    /// <summary>The file starts with a byte order mark, which <see cref="GetBytes"/> puts back.</summary>
    public bool HasMark { get; }

    /// <summary>
    /// Reads <paramref name="bytes"/> as text: the encoding and the text without its mark. False when they aren't valid
    /// text in the encoding their mark names, or in UTF-8 without one.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out TextFileEncoding encoding, out string text)
    {
        foreach (var marked in Marked)
        {
            var mark = marked.Preamble;
            if (bytes.StartsWith(mark))
            {
                encoding = new TextFileEncoding(marked, hasMark: true);
                return TryDecode(marked, bytes[mark.Length..], out text);
            }
        }
        encoding = new TextFileEncoding(Unmarked, hasMark: false);
        return TryDecode(Unmarked, bytes, out text);
    }

    /// <summary><paramref name="text"/> in this encoding, after the byte order mark if the file had one.</summary>
    public byte[] GetBytes(string text)
    {
        var mark = HasMark ? Encoding.Preamble : [];
        var bytes = new byte[mark.Length + Encoding.GetByteCount(text)];
        mark.CopyTo(bytes);
        Encoding.GetBytes(text, bytes.AsSpan(mark.Length));
        return bytes;
    }

    private static bool TryDecode(Encoding encoding, ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }
}
