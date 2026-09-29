namespace Claudette.Core.Protocol;

/// <summary>
/// An image in a user message: attached in the composer (DESIGN.md §5, "Attachments"), or read back from a
/// transcript. Sent to Claude Code as a base64 <c>image</c> content block.
/// </summary>
/// <param name="MediaType"><c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c> or <c>image/webp</c>.</param>
public sealed record MessageImage(string MediaType, byte[] Data)
{
    /// <summary>The media types the Messages API accepts.</summary>
    public static IReadOnlyList<string> SupportedMediaTypes { get; } = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>The media type of an image, from its first bytes, or null when it isn't one the API accepts.</summary>
    public static string? DetectMediaType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    /// <summary>The image from a base64 content block, or null when it isn't a valid one.</summary>
    public static MessageImage? FromBase64(string? mediaType, string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }
        try
        {
            var bytes = Convert.FromBase64String(data);
            return new MessageImage(mediaType ?? DetectMediaType(bytes) ?? "image/png", bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
