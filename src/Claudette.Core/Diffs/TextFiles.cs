using System.Text;

namespace Claudette.Core.Diffs;

internal enum TextFileKind
{
    Text,
    Missing,
    TooLarge,
    Binary,
    Unreadable,
}

internal readonly record struct TextFileRead(TextFileKind Kind, string? Text);

/// <summary>Reads a file for diffing: text only, up to a size cap.</summary>
internal static class TextFiles
{
    /// <summary>Files larger than this aren't diffed (DESIGN.md §8).</summary>
    public const long DefaultMaxBytes = 5 * 1024 * 1024;

    public static TextFileRead Read(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new TextFileRead(TextFileKind.Missing, null);
            }
            if (info.Length > maxBytes)
            {
                return new TextFileRead(TextFileKind.TooLarge, null);
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > maxBytes)
            {
                return new TextFileRead(TextFileKind.TooLarge, null);
            }
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return LineDiff.LooksBinary(text)
                ? new TextFileRead(TextFileKind.Binary, null)
                : new TextFileRead(TextFileKind.Text, text);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TextFileRead(TextFileKind.Missing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new TextFileRead(TextFileKind.Unreadable, null);
        }
    }
}
