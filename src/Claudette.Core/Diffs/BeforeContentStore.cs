using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Claudette.Core.Diffs;

/// <summary>
/// The "before" content of large files, kept for replaying transcripts (DESIGN.md §8, "Before content"). Claude Code
/// sends a file's whole <c>originalFile</c> live, but writes it to the transcript as <c>null</c> when it's over
/// <see cref="TranscriptLimit"/> characters. So the first change's <c>originalFile</c> is saved here, named by its tool
/// call's id, and a restored tab, or the session opened again from History, finds it again.
/// </summary>
public sealed partial class BeforeContentStore(string directory, TimeProvider timeProvider)
{
    /// <summary>The longest <c>originalFile</c> Claude Code keeps in a transcript (2.1.284).</summary>
    public const int TranscriptLimit = 10_000;

    /// <summary>Saved content that hasn't been used for this long is deleted at launch.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private const string Extension = ".txt.gz";

    /// <summary>Keeps the content a file had before the change <paramref name="toolUseId"/>.</summary>
    public void Save(string toolUseId, string content)
    {
        if (FileFor(toolUseId) is not { } path)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(directory);
            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionLevel.Fastest);
            using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
            writer.Write(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a replay needs it; the file's "before" is unknown then.
        }
    }

    /// <summary>The content saved for the change <paramref name="toolUseId"/>, if any. Using it keeps it from being deleted as old.</summary>
    public string? Load(string toolUseId)
    {
        if (FileFor(toolUseId) is not { } path || !File.Exists(path))
        {
            return null;
        }
        try
        {
            string content;
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }
            File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
            return content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Deletes saved content in <paramref name="folder"/> that hasn't been used for <see cref="KeepFor"/>.</summary>
    public static void DeleteOld(string folder, DateTimeOffset now)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*" + Extension))
        {
            try
            {
                if (now - file.LastWriteTimeUtc > KeepFor)
                {
                    file.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use; next time.
            }
        }
    }

    /// <summary>Tool use ids come from Claude Code, so only plain ones name a file.</summary>
    private string? FileFor(string toolUseId) =>
        SafeId().IsMatch(toolUseId) ? Path.Combine(directory, toolUseId + Extension) : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
