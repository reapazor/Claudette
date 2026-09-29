namespace Claudette.Core.Diffs;

/// <summary>Looks for files without running anything, for diff tool detection (DESIGN.md §8).</summary>
public interface IFileProbe
{
    bool FileExists(string path);

    /// <summary>The full path of <paramref name="fileName"/> in the first <c>PATH</c> folder that has it, or null.</summary>
    string? FindOnPath(string fileName);

    /// <summary>Replaces <c>%NAME%</c> with the environment variable's value; unknown variables are left as they are.</summary>
    string ExpandEnvironmentVariables(string path);
}

/// <summary>The real file system and environment.</summary>
public sealed class FileProbe : IFileProbe
{
    public static FileProbe Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    public string ExpandEnvironmentVariables(string path) => Environment.ExpandEnvironmentVariables(path);
}
