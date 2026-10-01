namespace Claudette.Core.Settings;

/// <summary>
/// Claudette's settings (DESIGN.md §14). Stored as <c>settings.json</c>; separate from Claude Code's own settings.
/// Categories that belong to later milestones are added with those milestones.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public GeneralSettings General { get; set; } = new();

    public ClaudeCodeSettings ClaudeCode { get; set; } = new();

    public NewTabSettings NewTabs { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public SessionSettings Sessions { get; set; } = new();

    public CheckInSettings CheckIns { get; set; } = new();

    public List<QuickSuffix> QuickSuffixes { get; set; } = QuickSuffix.Defaults();

    public UsageSettings Usage { get; set; } = new();

    public ProcessSettings Processes { get; set; } = new();

    public DiffToolSettings DiffTool { get; set; } = new();

    /// <summary>Settings → Project tools (DESIGN.md §18). Per machine, like the diff tool.</summary>
    public ProjectToolSettings ProjectTools { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public KeyboardSettings Keyboard { get; set; } = new();

    public AdvancedSettings Advanced { get; set; } = new();

    public PerforceSettings Perforce { get; set; } = new();
}

/// <summary>
/// OS notifications, each type on or off, and the Dock/taskbar badge (DESIGN.md §10). Check-in notifications are a
/// check-in setting (<see cref="CheckInSettings.Notify"/>), so a tab can override them.
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>A tab finished its turn while you weren't looking at it.</summary>
    public bool TurnFinished { get; set; } = true;

    /// <summary>A permission prompt, question or plan is waiting.</summary>
    public bool NeedsInput { get; set; } = true;

    /// <summary>A tab's Claude Code stopped with an error or exited unexpectedly.</summary>
    public bool ProcessErrors { get; set; } = true;

    /// <summary>Usage thresholds, the projection and limit resets (DESIGN.md §6, "Alerts").</summary>
    public bool UsageAlerts { get; set; } = true;

    public bool SignIn { get; set; } = true;

    public bool UpdateReady { get; set; } = true;

    /// <summary>A long project action, such as a build, finished or failed (DESIGN.md §18, "Project tools").</summary>
    public bool ProjectActions { get; set; } = true;

    /// <summary>The number of tabs needing input on the Dock icon or taskbar button.</summary>
    public bool Badge { get; set; } = true;

    /// <summary>
    /// The Dock icon or taskbar button moves while tabs work or wait (DESIGN.md §10): the taskbar overlay's spark, and an
    /// hourglass while a tab waits for a usage limit to reset (Windows); or Claudette typing, waving while a tab needs
    /// input, and waiting by an hourglass (macOS).
    /// </summary>
    public bool AnimateIcon { get; set; } = true;
}

/// <summary>How long to keep something: usage history (DESIGN.md §6) or library sessions (§9).</summary>
public enum RetentionPeriod
{
    OneDay,
    OneWeek,
    OneMonth,
    OneYear,
    Forever,
}

public static class RetentionPeriodExtensions
{
    /// <summary>Null means forever.</summary>
    public static TimeSpan? ToTimeSpan(this RetentionPeriod period) => period switch
    {
        RetentionPeriod.OneDay => TimeSpan.FromDays(1),
        RetentionPeriod.OneWeek => TimeSpan.FromDays(7),
        RetentionPeriod.OneMonth => TimeSpan.FromDays(31),
        RetentionPeriod.OneYear => TimeSpan.FromDays(366),
        _ => null,
    };

    public static string Label(this RetentionPeriod period) => period switch
    {
        RetentionPeriod.OneDay => "1 day",
        RetentionPeriod.OneWeek => "1 week",
        RetentionPeriod.OneMonth => "1 month",
        RetentionPeriod.OneYear => "1 year",
        _ => "Forever",
    };
}

/// <summary>DESIGN.md §6 and the Usage row of §14.</summary>
public sealed class UsageSettings
{
    /// <summary>The session meter turns amber here.</summary>
    public double WarnPercent { get; set; } = 75;

    /// <summary>The session meter turns red here.</summary>
    public double CriticalPercent { get; set; } = 90;

    /// <summary>The burn rate is measured over this many recent minutes.</summary>
    public int BurnRateWindowMinutes { get; set; } = 30;

    /// <summary>Show model-specific weekly limits (for example Fable).</summary>
    public bool ShowModelMeters { get; set; } = true;

    /// <summary>If <c>get_usage</c> stops working, read model-specific limits from <c>/usage</c> text instead.</summary>
    public bool UseUsageCommandFallback { get; set; }

    /// <summary>
    /// When a usage limit stops a task, continue it once the limit resets (DESIGN.md §6, "Continuing after a limit
    /// resets"). Each tab can override it.
    /// </summary>
    public bool ContinueAfterLimitReset { get; set; } = true;

    public RetentionPeriod KeepHistory { get; set; } = RetentionPeriod.OneMonth;

    /// <summary>
    /// Share this machine's plan usage readings through the session library, and chart those of the other machines signed
    /// in to the same account (DESIGN.md §6, "Sharing across machines"). Off by default.
    /// </summary>
    public bool ShareThroughLibrary { get; set; }
}

/// <summary>DESIGN.md §4, "Process monitor". Off by default.</summary>
public sealed class ProcessSettings
{
    public bool ShowMonitor { get; set; }

    /// <summary>How often the Processes panel refreshes while visible. The composer summary refreshes every 10 seconds.</summary>
    public int RefreshSeconds { get; set; } = 2;

    /// <summary>Command lines can contain tokens or passwords, so they can be hidden.</summary>
    public bool ShowCommandLines { get; set; } = true;
}

/// <summary>How changed files open (DESIGN.md §8, "External diff tool"). Per machine: program paths differ.</summary>
public sealed class DiffToolSettings
{
    /// <summary><c>builtIn</c>, <c>preset</c> or <c>custom</c>.</summary>
    public string Kind { get; set; } = "builtIn";

    public string? PresetId { get; set; }

    /// <summary>A program and arguments with <c>{left}</c>, <c>{right}</c>, <c>{leftTitle}</c> and <c>{rightTitle}</c>.</summary>
    public string? CustomCommand { get; set; }
}

public sealed class GeneralSettings
{
    public bool ConfirmCloseWorkingTab { get; set; } = true;

    /// <summary>Also send tab renames to Claude Code, so <c>claude --resume &lt;name&gt;</c> sees them.</summary>
    public bool RenameInClaudeCode { get; set; }

    /// <summary>Check GitHub for new Claudette releases at launch and every few hours (DESIGN.md §2, "Updating Claudette").</summary>
    public bool CheckForAppUpdates { get; set; } = true;

    /// <summary>Offer pre-releases too, not only full releases.</summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// Show Claude's service status from status.claude.com: a dot in the header, and a banner while Claude has an
    /// incident (DESIGN.md §18, "Service status"). Off stops the checks too.
    /// </summary>
    public bool ShowServiceStatus { get; set; } = true;
}

public sealed class ClaudeCodeSettings
{
    /// <summary>Null means find it automatically.</summary>
    public string? ClaudePath { get; set; }

    /// <summary>Check for Claude Code updates at launch and every few hours (DESIGN.md §12).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// On macOS and Linux, give Claude and the tools Claudette runs the login shell's environment when Claudette wasn't
    /// started from a terminal (DESIGN.md §13, "Login shell environment").
    /// </summary>
    public bool UseLoginShellEnvironment { get; set; } = true;

    /// <summary>
    /// New tabs start with <see cref="TabState.RemoteControl"/> on, connected to the Claude app (DESIGN.md §18, "Remote
    /// Control"). Off by default; it never changes open tabs.
    /// </summary>
    public bool ConnectNewTabsToClaudeApp { get; set; }

    /// <summary>Keep the computer from sleeping while a tab is connected to the Claude app (DESIGN.md §18). The display can still sleep.</summary>
    public bool KeepAwakeWhileConnected { get; set; } = true;

    /// <summary>
    /// A model alias or id Claude Code falls back to when the tab's model is overloaded (<c>--fallback-model</c>); null
    /// for none. Applies to tabs started after the change.
    /// </summary>
    public string? FallbackModel { get; set; }

    /// <summary>
    /// Claude Code keeps a copy of each file before Claude changes it, so a prompt's changes can be put back (DESIGN.md
    /// §5, "Rewind and branch"). On by default, as in Claude Code's terminal.
    /// </summary>
    public bool KeepFileCheckpoints { get; set; } = true;

    /// <summary>A row in the conversation for every hook run, not only those that fail or print (DESIGN.md §5, "Hook runs").</summary>
    public bool ShowAllHookRuns { get; set; }
}

public sealed class NewTabSettings
{
    /// <summary>A model alias or id; null uses Claude Code's default.</summary>
    public string? DefaultModel { get; set; }

    public string? DefaultEffort { get; set; }

    public string? DefaultPermissionMode { get; set; }

    public int RecentFolderLimit { get; set; } = 20;
}

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>Settings → Appearance → Style (DESIGN.md §3, "Visual style").</summary>
public enum AppStyle
{
    /// <summary>Claudette's own look, after Claude Code's VS Code extension: neutral greys and the OS's accent color.</summary>
    Standard,

    /// <summary>The Claude apps' look: ivory and warm greys, Claude's orange, message bubbles and serif replies.</summary>
    Claude,
}

public sealed class AppearanceSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>The look, in light and dark alike (DESIGN.md §3, "Visual style").</summary>
    public AppStyle Style { get; set; } = AppStyle.Standard;

    public double ConversationFontSize { get; set; } = 14;

    public double CodeFontSize { get; set; } = 13;

    /// <summary>The conversation's font family, or null for Claudette's own (DESIGN.md §14, "Appearance").</summary>
    public string? ConversationFont { get; set; }

    /// <summary>The font family for code, commands and diffs, or null for the default monospace fonts.</summary>
    public string? CodeFont { get; set; }

    public bool ExpandThinking { get; set; }

    /// <summary>The working line's twinkling glyph and fun verbs (DESIGN.md §5, "Working line"); off shows "Working…".</summary>
    public bool FunWorkingWords { get; set; } = true;

    /// <summary>The working line says what the running tool is doing, such as "Running dotnet test…".</summary>
    public bool ShowToolInWorkingLine { get; set; } = true;

    /// <summary>A small ring on each tab's row showing how full its context is (DESIGN.md §4, "Sidebar").</summary>
    public bool ShowContextOnTabs { get; set; } = true;

    /// <summary>How much room the conversation, the sidebar's rows and the composer take (DESIGN.md §14).</summary>
    public Density Density { get; set; } = Density.Comfortable;
}

/// <summary>Settings → Appearance → Density: Compact tightens spacing and padding (DESIGN.md §14).</summary>
public enum Density
{
    Comfortable,
    Compact,
}

public sealed class SessionSettings
{
    /// <summary>Pinned tabs are always restored; this also restores the unpinned ones (DESIGN.md §9).</summary>
    public bool RestoreUnpinnedTabs { get; set; }

    /// <summary>The session library folder (DESIGN.md §9). Null uses the app data folder.</summary>
    public string? LibraryFolder { get; set; }

    /// <summary>This machine's name in History and leases. Null uses the computer name.</summary>
    public string? MachineName { get; set; }

    /// <summary>How long sessions stay in the library after they were last used.</summary>
    public RetentionPeriod KeepLibrarySessions { get; set; } = RetentionPeriod.Forever;

    /// <summary>
    /// New tabs start with <see cref="TabState.SyncToLibrary"/> on (DESIGN.md §9, "Session library"). Off by default,
    /// so a tab copies its session to the library only when it opts in.
    /// </summary>
    public bool SyncNewTabs { get; set; }

    /// <summary>Sync Claudette's settings through the library (DESIGN.md §14, "Settings sync").</summary>
    public bool SyncSettings { get; set; }
}

/// <summary>DESIGN.md §5, "Check-ins on long turns".</summary>
public sealed class CheckInSettings
{
    public const string DefaultMessage =
        "Everything OK? Give me a one or two sentence status update: what you're doing, and whether you're stuck or waiting on something.";

    public bool Enabled { get; set; } = true;

    /// <summary>Check in after the turn has run this long. 0 turns this trigger off.</summary>
    public int RunTimeMinutes { get; set; } = 15;

    /// <summary>Check in after the turn has produced no output for this long. 0 turns this trigger off.</summary>
    public int QuietTimeMinutes { get; set; } = 5;

    public string Message { get; set; } = DefaultMessage;

    /// <summary>Also send an OS notification when a check-in is sent.</summary>
    public bool Notify { get; set; }

    public CheckInSettings Clone() => (CheckInSettings)MemberwiseClone();
}

/// <summary>A saved snippet that can be appended to a message (DESIGN.md §5, "Quick suffixes").</summary>
public sealed class QuickSuffix
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Label { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>Adds this suffix straight from the keyboard, as a <see cref="KeyChord"/> such as <c>Primary+Alt+1</c>.</summary>
    public string? Shortcut { get; set; }

    public static List<QuickSuffix> Defaults() =>
    [
        new() { Id = "clarify", Label = "Clarify first", Text = "Ask clarifying questions before you start." },
        new() { Id = "plan", Label = "Plan only", Text = "Don't change any files yet. Explain your plan and wait for me to approve it." },
        new() { Id = "small", Label = "Keep it small", Text = "Keep the change as small as possible and don't refactor unrelated code." },
        new() { Id = "test", Label = "Test it", Text = "Run the relevant tests when you're done and fix any failures." },
        new() { Id = "explain", Label = "Explain", Text = "Explain what you changed and why when you're done." },
    ];
}

public sealed class AdvancedSettings
{
    /// <summary>Extra command-line arguments for every <c>claude</c> process, split on spaces.</summary>
    public string ExtraArguments { get; set; } = "";

    /// <summary>
    /// Writes each session's raw protocol traffic to the log folder, and shows messages Claudette skipped in the
    /// conversation (DESIGN.md §13, "Logging"; §16).
    /// </summary>
    public bool LogProtocol { get; set; }
}
