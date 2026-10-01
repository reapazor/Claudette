using Claudette.Core.Settings;

namespace Claudette.App.ViewModels.Settings;

/// <summary>Settings → Notifications (DESIGN.md §10): each kind on or off, the Dock or taskbar badge and the icon.</summary>
public sealed class NotificationsPage(SettingsContext context) : SettingsPage(context, SettingsCategory.Notifications)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("A tab finishes its turn"),
        Entry("A tab needs permission or an answer"),
        Entry("A tab's Claude Code stops with an error"),
        Entry("Usage alerts", pageText: "Usage crosses a warning level"),
        Entry("Claude Code needs me to sign in"),
        Entry("A Claude Code update is ready"),
        Entry("A project action finishes"),
        Entry("Dock or taskbar badge", pageText: "Show the number of tabs needing input on the Dock or taskbar icon"),
        Entry("Animate the Dock or taskbar icon"),
    ];

    /// <summary>False when this machine can't show OS notifications, for example a macOS build run outside its app bundle.</summary>
    public bool NotificationsAvailable => Services.Notifications.IsAvailable;

    public bool NotifyTurnFinished
    {
        get => Settings.Notifications.TurnFinished;
        set => Set(value, v => Settings.Notifications.TurnFinished = v);
    }

    public bool NotifyNeedsInput
    {
        get => Settings.Notifications.NeedsInput;
        set => Set(value, v => Settings.Notifications.NeedsInput = v);
    }

    public bool NotifyProcessErrors
    {
        get => Settings.Notifications.ProcessErrors;
        set => Set(value, v => Settings.Notifications.ProcessErrors = v);
    }

    public bool NotifyUsageAlerts
    {
        get => Settings.Notifications.UsageAlerts;
        set => Set(value, v => Settings.Notifications.UsageAlerts = v);
    }

    public bool NotifySignIn
    {
        get => Settings.Notifications.SignIn;
        set => Set(value, v => Settings.Notifications.SignIn = v);
    }

    public bool NotifyUpdateReady
    {
        get => Settings.Notifications.UpdateReady;
        set => Set(value, v => Settings.Notifications.UpdateReady = v);
    }

    /// <summary><b>A project action finishes</b> (DESIGN.md §18, "Project tools").</summary>
    public bool NotifyProjectActions
    {
        get => Settings.Notifications.ProjectActions;
        set => Set(value, v => Settings.Notifications.ProjectActions = v);
    }

    public bool ShowBadge
    {
        get => Settings.Notifications.Badge;
        set => Set(value, v => Settings.Notifications.Badge = v);
    }

    public bool AnimateIcon
    {
        get => Settings.Notifications.AnimateIcon;
        set => Set(value, v => Settings.Notifications.AnimateIcon = v);
    }

    protected override void ResetSettings()
    {
        Settings.Notifications = new NotificationSettings();
        Save();
    }
}
