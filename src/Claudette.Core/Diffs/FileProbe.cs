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
/// <param name="pathVariable">
/// The <c>PATH</c> to search. By default, Claudette's own; the user environment's probe searches the one the user's
/// processes get, which can come from the login shell (DESIGN.md §13, "Login shell environment").
/// </param>
public sealed class FileProbe(Func<string?>? pathVariable = null) : IFileProbe
{
    public static FileProbe Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public string? FindOnPath(string fileName) => FindIn(pathVariable is null ? Environment.GetEnvironmentVariable("PATH") : pathVariable(), fileName);

    /// <summary>The full path of <paramref name="fileName"/> in the first folder of <paramref name="pathValue"/> that has it, or null.</summary>
    public static string? FindIn(string? pathValue, string fileName)
    {
        foreach (var directory in (pathValue ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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
