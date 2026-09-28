namespace Claudette.Core;

/// <summary>Where Claudette keeps its files. Injected everywhere, so tests use temporary folders (DESIGN.md §15).</summary>
public sealed record AppPaths(string DataDirectory)
{
    /// <summary>The working folder of the hidden utility session.</summary>
    public string UtilityDirectory => Path.Combine(DataDirectory, "utility");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// The per-user default: <c>%LOCALAPPDATA%\Claudette</c> on Windows, <c>~/Library/Application Support/Claudette</c>
    /// on macOS and <c>~/.local/share/claudette</c> on Linux.
    /// </summary>
    public static AppPaths ForCurrentUser()
    {
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new AppPaths(Path.Combine(baseDirectory, OperatingSystem.IsLinux() ? "claudette" : "Claudette"));
    }
}
