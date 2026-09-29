using Claudette.Core.Protocol;

namespace Claudette.Core.Composer;

/// <summary>
/// What a dropped or pasted file becomes (DESIGN.md §5, "Attachments"): an image sent with the message, an <c>@path</c>
/// mention for Claude Code to read, or an error to show.
/// </summary>
public sealed record Attachment
{
    /// <summary>A file name, for the thumbnail's tooltip and error messages.</summary>
    public required string Name { get; init; }

    public MessageImage? Image { get; init; }

    /// <summary>The <c>@path</c> to insert into the message.</summary>
    public string? Mention { get; init; }

    public string? Error { get; init; }

    public static Attachment Failed(string name, string error) => new() { Name = name, Error = error };
}

/// <summary>
/// Turns dropped and pasted files into attachments (DESIGN.md §5, "Attachments").
/// <list type="bullet">
/// <item>PNG, JPEG, GIF and WebP images are attached as images, up to <see cref="MaxImageBytes"/> each and
/// <see cref="MaxImagesPerMessage"/> per message. Claude Code scales large ones down itself.</item>
/// <item>Other files and folders become <c>@path</c> mentions, relative to the working folder when they're in it.
/// Claude Code reads them when the message is sent: text, PDFs, notebooks, images and folder listings.</item>
/// <item>Binary files Claude can't read are refused, instead of a mention Claude Code would silently drop.</item>
/// </list>
/// </summary>
public static class Attachments
{
    /// <summary>Per image. Claude Code re-encodes large images to fit the API's limits, so this only bounds memory.</summary>
    public const long MaxImageBytes = 20 * 1024 * 1024;

    /// <summary>The claude.ai limit per message; more than 20 images in a request also tightens the API's size rules.</summary>
    public const int MaxImagesPerMessage = 20;

    /// <summary>How much of a file is read to tell text from binary.</summary>
    private const int SniffBytes = 8192;

    /// <summary>Formats Claude Code reads that contain zero bytes.</summary>
    private static readonly HashSet<string> ReadableBinaryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public static string SupportedImagesText => "PNG, JPEG, GIF or WebP";

    /// <summary>An image from the clipboard or a drag, as bytes (Avalonia gives a pasted bitmap as PNG).</summary>
    public static Attachment FromImageBytes(byte[] data, string name)
    {
        if (data.LongLength > MaxImageBytes)
        {
            return Attachment.Failed(name, $"{name} is {Megabytes(data.LongLength)}; images can be up to {Megabytes(MaxImageBytes)}.");
        }
        return MessageImage.DetectMediaType(data) is { } mediaType
            ? new Attachment { Name = name, Image = new MessageImage(mediaType, data) }
            : Attachment.Failed(name, $"{name} isn't an image Claude can read. Use {SupportedImagesText}.");
    }

    /// <summary>A dropped or pasted file or folder.</summary>
    /// <param name="workingFolder">The tab's folder: paths inside it are mentioned relative to it.</param>
    public static async Task<Attachment> FromPathAsync(string path, string workingFolder, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length == 0)
        {
            name = path;
        }
        try
        {
            if (Directory.Exists(path))
            {
                return new Attachment { Name = name, Mention = ComposerTokens.Mention(MentionPath(path, workingFolder).TrimEnd('/') + "/") };
            }
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Attachment.Failed(name, $"{name} no longer exists.");
            }

            var head = new byte[(int)Math.Min(SniffBytes, info.Length)];
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            {
                head = head[..await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)];
            }
            if (MessageImage.DetectMediaType(head) is not null)
            {
                if (info.Length > MaxImageBytes)
                {
                    return Attachment.Failed(name, $"{name} is {Megabytes(info.Length)}; images can be up to {Megabytes(MaxImageBytes)}.");
                }
                return FromImageBytes(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), name);
            }
            if (Array.IndexOf(head, (byte)0) >= 0 && !ReadableBinaryExtensions.Contains(Path.GetExtension(path)))
            {
                return Attachment.Failed(name, $"Claude can't read {name}. Attach text files, PDFs, folders, or {SupportedImagesText} images.");
            }
            return new Attachment { Name = name, Mention = ComposerTokens.Mention(MentionPath(path, workingFolder)) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Attachment.Failed(name, $"Couldn't read {name}: {ex.Message}");
        }
    }

    /// <summary>Relative to the working folder with forward slashes when it's inside it, else the full path.</summary>
    public static string MentionPath(string path, string workingFolder)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Path.GetFullPath(workingFolder), full);
        var inside = relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        return inside ? relative.Replace(Path.DirectorySeparatorChar, '/') : full;
    }

    /// <summary>Rounded up to a tenth, so an image just over the limit never reads as the limit itself.</summary>
    private static string Megabytes(long bytes) => $"{Math.Ceiling(bytes * 10 / (1024.0 * 1024)) / 10:0.#} MB";
}
