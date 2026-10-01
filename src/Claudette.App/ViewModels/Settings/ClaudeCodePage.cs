using Claudette.Core.Installation;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// Settings → Claude Code (DESIGN.md §14): the installed version and its updates, the account, how tabs run (with
/// folder trust), the login shell's environment, and the Claude app. Dispose it with the window.
/// </summary>
public sealed partial class ClaudeCodePage : SettingsPage, IDisposable
{
    private readonly NewTabsPage _newTabs;

    /// <param name="newTabs">New tabs, whose model list <b>Fallback model</b> offers too.</param>
    public ClaudeCodePage(SettingsContext context, string? accountText, ClaudeUpdateViewModel? updates, NewTabsPage newTabs) : base(context, SettingsCategory.ClaudeCode)
    {
        _newTabs = newTabs;
        AccountText = accountText ?? "Not signed in";
        Updates = updates;
        // Signing in from this window can make the Claude app available, or not (DESIGN.md §18).
        Services.RemoteControl.AvailabilityChanged += OnRemoteControlAvailabilityChanged;
    }

    public void Dispose() => Services.RemoteControl.AvailabilityChanged -= OnRemoteControlAvailabilityChanged;

    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Installed version and install method", pageText: "installed with"),
        Entry("Check for Claude Code updates automatically"),
        Entry("Update Claude Code", pageText: "Update now"),
        Entry("Signed-in account", pageText: "Signed in as"),
        Entry("Sign in"),
        Entry("Sign out"),
        Entry("Path to claude"),
        Entry("Fallback model"),
        Entry("Keep copies of files Claude changes, so prompts can be rewound"),
        Entry("Show every hook run in the conversation"),
        Entry("Summarize subagents' progress"),
        Entry("Ask before using a new folder's own configuration (folder trust)", pageText: "Ask before using a new folder's own configuration"),
        .. ShowLoginShellSetting ? [Entry("Use my login shell's environment")] : Array.Empty<SettingsSearchResult>(),
        Entry("Connect new tabs to the Claude app (Remote Control)", pageText: "Connect new tabs to the Claude app"),
        Entry("Push notifications on your phone", pageText: "push notification on your phone"),
        Entry("Keep this computer awake while tabs are connected"),
    ];

    // ---- The installed version and the account ----------------------------------------------------------------------

    public string AccountText { get; }

    /// <summary>The account, with <b>Sign in</b> and <b>Sign out</b> wired to the header's (DESIGN.md §11). Null in some tests.</summary>
    public AccountViewModel? Account { get; internal set; }

    public bool HasAccount => Account is not null;

    /// <summary>Version, install method, update checks and <b>Update now</b> (DESIGN.md §12, §14). Null before Claude Code is found.</summary>
    public ClaudeUpdateViewModel? Updates { get; }

    public bool HasUpdates => Updates is not null;

    public bool CheckForUpdates
    {
        get => Settings.ClaudeCode.CheckForUpdates;
        set => Set(value, v => Settings.ClaudeCode.CheckForUpdates = v);
    }

    public string InstalledText => Services.Install is { } install
        ? $"Claude Code {install.Version} at {install.Path}. Last tested with {ClaudeLocator.LastTestedVersion}"
            + (install.Version > ClaudeLocator.LastTestedVersion ? " (this version is newer)."
                : install.Version < ClaudeLocator.LastTestedVersion ? " (this version is older; updating is recommended)."
                : ".")
        : "Claude Code wasn't found.";

    // ---- How tabs run (DESIGN.md §5, "Rewind and branch", "Hook runs"; §13, "Launch") ----------------------------

    /// <summary><b>Fallback model</b>: none, or one of the models New tabs offers.</summary>
    public IReadOnlyList<Choice> FallbackModelChoices => field ??=
        [new(null, "None"), .. _newTabs.ModelChoices.Where(c => c.Value is not null)];

    public Choice FallbackModel
    {
        get => FallbackModelChoices.FirstOrDefault(c => c.Value == Settings.ClaudeCode.FallbackModel)
            ?? (Settings.ClaudeCode.FallbackModel is { } custom ? new Choice(custom, custom) : FallbackModelChoices[0]);
        set => Set(value?.Value, v => Settings.ClaudeCode.FallbackModel = v);
    }

    /// <summary><b>Keep copies of files Claude changes, so prompts can be rewound</b>.</summary>
    public bool KeepFileCheckpoints
    {
        get => Settings.ClaudeCode.KeepFileCheckpoints;
        set => Set(value, v => Settings.ClaudeCode.KeepFileCheckpoints = v);
    }

    /// <summary><b>Show every hook run in the conversation</b>.</summary>
    public bool ShowAllHookRuns
    {
        get => Settings.ClaudeCode.ShowAllHookRuns;
        set => Set(value, v => Settings.ClaudeCode.ShowAllHookRuns = v);
    }

    /// <summary><b>Summarize subagents' progress</b> (DESIGN.md §18, "Agent map").</summary>
    public bool SubagentProgressSummaries
    {
        get => Settings.ClaudeCode.SubagentProgressSummaries;
        set => Set(value, v => Settings.ClaudeCode.SubagentProgressSummaries = v);
    }

    /// <summary><b>Ask before using a new folder's own configuration</b> (DESIGN.md §7, "Folder trust").</summary>
    public bool AskBeforeUsingFolderSettings
    {
        get => Settings.ClaudeCode.AskBeforeUsingFolderSettings;
        set => Set(value, v => Settings.ClaudeCode.AskBeforeUsingFolderSettings = v);
    }

    /// <summary>
    /// <b>Use my login shell's environment</b> (DESIGN.md §13, "Login shell environment"). Turning it on reads the login
    /// shell then, if this run hasn't yet; either way it applies to processes started after.
    /// </summary>
    public bool UseLoginShellEnvironment
    {
        get => Settings.ClaudeCode.UseLoginShellEnvironment;
        set => Set(value, v => Settings.ClaudeCode.UseLoginShellEnvironment = v);
    }

    /// <summary>Only macOS and Linux have the setting: on Windows, apps get the user's full environment.</summary>
    public static bool ShowLoginShellSetting => !OperatingSystem.IsWindows();

    /// <summary>Null (empty) finds Claude Code automatically. Takes effect the next time Claudette starts.</summary>
    public string ClaudePath
    {
        get => Settings.ClaudeCode.ClaudePath ?? "";
        set => Set(value, v => Settings.ClaudeCode.ClaudePath = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    [RelayCommand]
    private async Task BrowseClaudePathAsync()
    {
        if (await Services.Platform.PickFileAsync("Choose the claude executable") is { } path)
        {
            ClaudePath = path;
            OnPropertyChanged(nameof(ClaudePath));
        }
    }

    // ---- The Claude app (DESIGN.md §18, "Remote Control") --------------------------------------------------------

    /// <summary>The docs' steps for pushes to the phone. Claudette doesn't change Claude Code's settings for them.</summary>
    public const string PushNotificationsDocs = "https://code.claude.com/docs/en/remote-control#mobile-push-notifications";

    /// <summary>
    /// <b>Connect new tabs to the Claude app</b>: the switch new tabs start with, like <b>Sync new tabs</b>. Open tabs
    /// keep theirs; each has <b>Connect to the Claude app</b> in its menu.
    /// </summary>
    public bool ConnectNewTabsToClaudeApp
    {
        get => Settings.ClaudeCode.ConnectNewTabsToClaudeApp;
        set => Set(value, v => Settings.ClaudeCode.ConnectNewTabsToClaudeApp = v);
    }

    /// <summary><b>Keep this computer awake while tabs are connected</b>: system sleep only; the display can still sleep.</summary>
    public bool KeepAwakeWhileConnected
    {
        get => Settings.ClaudeCode.KeepAwakeWhileConnected;
        set => Set(value, v => Settings.ClaudeCode.KeepAwakeWhileConnected = v);
    }

    /// <summary>The account can use Remote Control. When it can't, the setting and the tabs' switches are disabled.</summary>
    public bool CanUseRemoteControl => Services.RemoteControl.IsAvailable;

    /// <summary>Why the account can't use Remote Control, from <c>claude auth status</c> and the environment.</summary>
    public string? RemoteControlUnavailableText => Services.RemoteControl.UnavailableReason is { } reason ? $"Not available: {reason}" : null;

    public bool HasRemoteControlUnavailableText => RemoteControlUnavailableText is not null;

    [RelayCommand]
    private Task OpenPushNotificationsDocsAsync() => Services.Platform.OpenUrlAsync(PushNotificationsDocs);

    private void OnRemoteControlAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanUseRemoteControl));
        OnPropertyChanged(nameof(RemoteControlUnavailableText));
        OnPropertyChanged(nameof(HasRemoteControlUnavailableText));
    }

    protected override void ResetSettings()
    {
        Settings.ClaudeCode = new ClaudeCodeSettings();
        Save();
    }
}
