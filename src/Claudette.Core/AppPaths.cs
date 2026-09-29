namespace Claudette.Core;

/// <summary>Where Claudette keeps its files. Injected everywhere, so tests use temporary folders (DESIGN.md §15).</summary>
/// <param name="DataDirectory">Per-machine data: saved tabs, the utility session, logs.</param>
/// <param name="SettingsDirectory">Settings (DESIGN.md §14, "Storage"). Roams with the user profile on Windows.</param>
public sealed record AppPaths(string DataDirectory, string SettingsDirectory)
{
    /// <summary>The working folder of the hidden utility session.</summary>
    public string UtilityDirectory => Path.Combine(DataDirectory, "utility");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string StateFile => Path.Combine(DataDirectory, "state.json");

    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// The per-user defaults. Data: <c>%LOCALAPPDATA%\Claudette</c>, <c>~/Library/Application Support/Claudette</c>,
    /// <c>~/.local/share/claudette</c>. Settings: <c>%APPDATA%\Claudette</c>, the same Application Support folder on
    /// macOS, and <c>~/.config/claudette</c> on Linux.
    /// </summary>
    public static AppPaths ForCurrentUser()
    {
        // For development and UI checks: keep everything under one folder instead of the real profile.
        if (Environment.GetEnvironmentVariable("CLAUDETTE_HOME") is { Length: > 0 } home)
        {
            return Under(home);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        var name = OperatingSystem.IsLinux() ? "claudette" : "Claudette";
        return new AppPaths(Path.Combine(local, name), Path.Combine(OperatingSystem.IsMacOS() ? local : roaming, name));
    }

    /// <summary>Everything under one folder, for tests.</summary>
    public static AppPaths Under(string root) => new(Path.Combine(root, "data"), Path.Combine(root, "settings"));
}
