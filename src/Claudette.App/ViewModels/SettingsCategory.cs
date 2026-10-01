namespace Claudette.App.ViewModels;

/// <summary>
/// The Settings window's categories, by the names its sidebar shows (DESIGN.md §14). Use these rather than the names
/// written out, so a page, its search entries and a link to it can't disagree. The selected tab's project pages are
/// named by the project instead (<see cref="SettingsViewModel.ProjectPages"/>).
/// </summary>
public static class SettingsCategory
{
    public const string General = "General";

    public const string Sessions = "Sessions";

    public const string Processes = "Processes";

    public const string ClaudeCode = "Claude Code";

    public const string NewTabs = "New tabs";

    public const string Appearance = "Appearance";

    public const string Usage = "Usage";

    public const string QuickSuffixes = "Quick suffixes";

    public const string CheckIns = "Check-ins";

    public const string DiffTool = "Diff tool";

    public const string ProjectTools = "Project tools";

    public const string Notifications = "Notifications";

    public const string Keyboard = "Keyboard";

    public const string Perforce = "Perforce";

    public const string Advanced = "Advanced";
}
