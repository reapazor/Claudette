using System.Globalization;
using System.Text.Json;
using Claudette.Core.Settings;

namespace Claudette.Core.Development;

/// <summary>A copy of one build that Claudette runs from.</summary>
/// <param name="Directory">The copy.</param>
/// <param name="SourceOutput">The build output it was copied from.</param>
/// <param name="Stamp">When that build was written: its newest file's time, in UTC.</param>
public sealed record BuildCopy(string Directory, string SourceOutput, DateTime Stamp);

/// <summary>
/// Copies of the app's build output that a source build of Claudette runs from, so the build output itself isn't in
/// use and can be rebuilt while Claudette runs (DESIGN.md §9, "Working on Claudette"). One folder per build.
/// </summary>
/// <param name="root">Where the copies go, under the data folder.</param>
public sealed class BuildCopies(string root)
{
    public const string MarkerFileName = ".claudette-build.json";

    private const string RuntimesFolder = "runtimes";

    public string Root { get; } = root;

    /// <summary>
    /// Copies the build in <paramref name="outputDirectory"/>, with only this platform's native libraries, or returns
    /// the copy made earlier of the same build. Null if the build changed while it was being copied: it's still being
    /// written, so try again once it settles.
    /// </summary>
    public BuildCopy? Create(string outputDirectory, string runtimeIdentifier)
    {
        var stamp = BuildOutput.LatestWrite(outputDirectory) ?? throw new DirectoryNotFoundException($"No build in {outputDirectory}.");
        var target = Path.Combine(Root, stamp.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture));
        if (Read(target) is { } existing)
        {
            return existing;
        }
        var partial = target + ".partial";
        DeleteQuietly(partial);
        CopyFolder(outputDirectory, partial, runtimeIdentifier);
        if (BuildOutput.LatestWrite(outputDirectory) != stamp)
        {
            DeleteQuietly(partial);
            return null;
        }
        var copy = new BuildCopy(target, Path.GetFullPath(outputDirectory), stamp);
        File.WriteAllText(Path.Combine(partial, MarkerFileName), JsonSerializer.Serialize(copy, JsonFileStore<AppState>.Options));
        try
        {
            Directory.Move(partial, target);
        }
        catch (IOException) when (Read(target) is not null)
        {
            // Another Claudette copied the same build at the same moment.
            DeleteQuietly(partial);
        }
        return Read(target) ?? copy;
    }

    /// <summary>The copy in <paramref name="directory"/>, or null if it isn't a complete copy.</summary>
    public static BuildCopy? Read(string directory)
    {
        var marker = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(marker))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<BuildCopy>(File.ReadAllText(marker), JsonFileStore<AppState>.Options) is { } copy
                ? copy with { Directory = Path.GetFullPath(directory) }
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes old copies: all but <paramref name="current"/> and the newest other one, which the build that just
    /// restarted into this one may still be closing from. A copy still in use can't be deleted on Windows; it goes
    /// next time.
    /// </summary>
    public void CleanUp(string current)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }
        var running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        var others = Directory.EnumerateDirectories(Root)
            .Where(folder => !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), running, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var previous = others.Select(Read).OfType<BuildCopy>().MaxBy(copy => copy.Stamp)?.Directory;
        foreach (var folder in others.Where(folder => !string.Equals(Path.GetFullPath(folder), previous, StringComparison.OrdinalIgnoreCase)))
        {
            DeleteQuietly(folder);
        }
    }

    /// <summary>
    /// Whether a <c>runtimes</c> subfolder can be used on this platform: its own RID, or one it builds on, such as
    /// <c>win</c> for <c>win-x64</c> or <c>osx</c> for <c>osx-arm64</c>.
    /// </summary>
    internal static bool IsForPlatform(string runtimeFolder, string runtimeIdentifier) =>
        string.Equals(runtimeFolder, runtimeIdentifier, StringComparison.OrdinalIgnoreCase)
        || runtimeIdentifier.StartsWith(runtimeFolder + "-", StringComparison.OrdinalIgnoreCase)
        || (runtimeFolder == "unix" && !runtimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase));

    private static void CopyFolder(string from, string to, string runtimeIdentifier)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(folder);
            if (string.Equals(name, RuntimesFolder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var runtime in Directory.EnumerateDirectories(folder).Where(r => IsForPlatform(Path.GetFileName(r), runtimeIdentifier)))
                {
                    CopyFolder(runtime, Path.Combine(to, name, Path.GetFileName(runtime)), runtimeIdentifier);
                }
            }
            else
            {
                CopyFolder(folder, Path.Combine(to, name), runtimeIdentifier);
            }
        }
    }

    private static void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use; it goes next time.
        }
    }
}

/// <summary>Reading a build output folder.</summary>
public static class BuildOutput
{
    /// <summary>The time of the newest file directly in the folder, in UTC: a new build changes it. Null if there's no folder.</summary>
    public static DateTime? LatestWrite(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }
        DateTime? latest = null;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (file.Name != BuildCopies.MarkerFileName && (latest is null || file.LastWriteTimeUtc > latest))
            {
                latest = file.LastWriteTimeUtc;
            }
        }
        return latest;
    }

    /// <summary>Whether the folder holds a whole build of <paramref name="assemblyName"/>: its assembly and the files that start it.</summary>
    public static bool IsComplete(string directory, string assemblyName) =>
        File.Exists(Path.Combine(directory, assemblyName + ".dll"))
        && File.Exists(Path.Combine(directory, assemblyName + ".deps.json"))
        && File.Exists(Path.Combine(directory, assemblyName + ".runtimeconfig.json"));
}
