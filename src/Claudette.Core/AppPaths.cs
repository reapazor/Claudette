namespace Claudette.Core;

/// <summary>Where Claudette keeps its files. Injected everywhere, so tests use temporary folders (DESIGN.md §15).</summary>
/// <param name="DataDirectory">Per-machine data: saved tabs, the utility session, logs.</param>
/// <param name="SettingsDirectory">Settings (DESIGN.md §14, "Storage"). Roams with the user profile on Windows.</param>
public sealed record AppPaths(string DataDirectory, string SettingsDirectory)
{
    /// <summary>The working folder of the hidden utility session.</summary>
    public string UtilityDirectory => Path.Combine(DataDirectory, "utility");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Raw protocol logs, one per session, when Settings → Advanced turns them on (DESIGN.md §13).</summary>
    public string ProtocolLogDirectory => Path.Combine(LogDirectory, "protocol");

    public string StateFile => Path.Combine(DataDirectory, "state.json");

    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Usage history (DESIGN.md §6): per machine, never synced.</summary>
    public string UsageDatabase => Path.Combine(DataDirectory, "usage.db");

    /// <summary>The default session library, when no folder is chosen (DESIGN.md §9).</summary>
    public string DefaultLibraryDirectory => Path.Combine(DataDirectory, "library");

    /// <summary>Local working copies of library transcripts, which Claude Code resumes from and writes to.</summary>
    public string LocalSessionsDirectory => Path.Combine(DataDirectory, "sessions");

    /// <summary>"Before" files handed to external diff tools, per tab (DESIGN.md §8).</summary>
    public string DiffTempDirectory => Path.Combine(DataDirectory, "diff-temp");

    /// <summary>Large files' content before Claude's first change, which transcripts leave out (DESIGN.md §8, "Before content").</summary>
    public string BeforeContentDirectory => Path.Combine(DataDirectory, "before-content");

    /// <summary>Copies of a source build that Claudette runs from (DESIGN.md §9, "Working on Claudette").</summary>
    public string BuildCopiesDirectory => Path.Combine(DataDirectory, "builds");

    /// <summary>The state handed to a new build when Claudette restarts into it.</summary>
    public string RestartFile => Path.Combine(DataDirectory, "restart.json");

    /// <summary>Where the new build says it has started.</summary>
    public string RestartReadyFile => Path.Combine(DataDirectory, "restart-ready");

    /// <summary>Files project actions write for Claudette to read, such as Unity's test results (DESIGN.md §18).</summary>
    public string ProjectJobsDirectory => Path.Combine(DataDirectory, "project-jobs");

    /// <summary>This machine's copy of each scratch pad (DESIGN.md §18, "Scratch pad").</summary>
    public string ScratchPadsDirectory => Path.Combine(DataDirectory, ScratchPads.ScratchPadStore.FolderName);

    /// <summary>Downloaded Claudette releases, one folder per version (DESIGN.md §2, "Updating Claudette").</summary>
    public string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    /// <summary>
    /// There while Claudette's window is in front. Every <c>claude</c> gets it as <c>CLAUDE_CLIENT_PRESENCE_FILE</c>, so
    /// Remote Control doesn't push to the phone then (DESIGN.md §10, §18).
    /// </summary>
    public string PresenceFile => Path.Combine(DataDirectory, "presence");

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
