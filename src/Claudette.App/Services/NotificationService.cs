using Claudette.Core.Settings;
using Claudette.Platform.Notifications;

namespace Claudette.App.Services;

/// <summary>The notification types of DESIGN.md §10, each turned on or off in Settings → Notifications.</summary>
public enum NotificationKind
{
    /// <summary>A tab finished its turn while you weren't looking at it.</summary>
    TurnFinished,
    /// <summary>A permission prompt, question or plan is waiting.</summary>
    NeedsInput,
    /// <summary>A tab's Claude Code stopped with an error or exited unexpectedly.</summary>
    ProcessError,
    UsageAlert,
    SignIn,
    UpdateReady,
    /// <summary>A check-in was sent (DESIGN.md §5); a check-in setting, so a tab can override it.</summary>
    CheckIn,
}

/// <summary>Where clicking a notification goes: a tab, or an app-wide screen.</summary>
public sealed record NotificationTarget(NotificationKind Kind, string? TabId);

/// <summary>
/// Decides which OS notifications to send and routes clicks back (DESIGN.md §10), and keeps the Dock/taskbar badge.
/// A notification is skipped when its type is off, or when Claudette is in front and already showing what it's about:
/// the tab it's about is selected, or, for app-wide ones, the header or screen that already says it. Use on the UI thread.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly AppServices _services;
    private readonly INotifier _notifier;
    private readonly Dictionary<string, NotificationTarget> _shown = new(StringComparer.Ordinal);
    private IAppBadge _badge = NullNotifier.Instance;
    private int _tabsNeedingInput;
    private int _badgeShown = -1;

    public NotificationService(AppServices services, INotifier notifier)
    {
        _services = services;
        _notifier = notifier;
        _notifier.Activated += OnNotifierActivated;
    }

    /// <summary>False when this machine can't show OS notifications (for example an unbundled macOS build).</summary>
    public bool IsAvailable => _notifier.IsAvailable;

    /// <summary>Whether Claudette's window is in front. Set by the main window.</summary>
    public bool IsAppActive { get; private set; }

    /// <summary>The selected tab's id. Set by the shell.</summary>
    public Func<string?> SelectedTabId { get; set; } = () => null;

    /// <summary>Raised on the UI thread when the user clicks one of Claudette's notifications.</summary>
    public event Action<NotificationTarget>? Activated;

    /// <summary>Sends a notification, unless its type is off or the user is already looking at it.</summary>
    /// <param name="tabId">The tab it's about, or null for app-wide ones.</param>
    /// <param name="key">Tells apart app-wide notifications of the same kind, such as different usage alerts.</param>
    /// <param name="checkIns">The tab's check-in settings, for <see cref="NotificationKind.CheckIn"/>.</param>
    /// <returns>Whether it was sent.</returns>
    public bool Notify(NotificationKind kind, string title, string body, string? tabId = null, string? key = null, CheckInSettings? checkIns = null)
    {
        if (!IsEnabled(kind, checkIns) || !_notifier.IsAvailable)
        {
            return false;
        }
        if (IsAppActive && (tabId is null || tabId == SelectedTabId()))
        {
            return false;
        }
        var id = Id(kind, tabId ?? key);
        _shown[id] = new NotificationTarget(kind, tabId);
        _notifier.Show(new OsNotification(id, title, body));
        return true;
    }

    public bool IsEnabled(NotificationKind kind, CheckInSettings? checkIns = null)
    {
        var settings = _services.Settings.Notifications;
        return kind switch
        {
            NotificationKind.TurnFinished => settings.TurnFinished,
            NotificationKind.NeedsInput => settings.NeedsInput,
            NotificationKind.ProcessError => settings.ProcessErrors,
            NotificationKind.UsageAlert => settings.UsageAlerts,
            NotificationKind.SignIn => settings.SignIn,
            NotificationKind.UpdateReady => settings.UpdateReady,
            NotificationKind.CheckIn => (checkIns ?? _services.Settings.CheckIns).Notify,
            _ => false,
        };
    }

    /// <summary>Takes away a tab's notifications of one kind, or all of them: the user has seen what they were about.</summary>
    public void ClearTab(string tabId, NotificationKind? kind = null) =>
        Clear(target => target.TabId == tabId && (kind is null || target.Kind == kind));

    /// <summary>Takes away app-wide notifications of one kind, for example once signed in again.</summary>
    public void Clear(NotificationKind kind) => Clear(target => target.TabId is null && target.Kind == kind);

    private void Clear(Func<NotificationTarget, bool> match)
    {
        foreach (var id in _shown.Where(s => match(s.Value)).Select(s => s.Key).ToArray())
        {
            _shown.Remove(id);
            _notifier.Remove(id);
        }
    }

    /// <summary>Claudette's window came to the front or went to the back.</summary>
    public void SetAppActive(bool active)
    {
        IsAppActive = active;
        if (active && SelectedTabId() is { } selected)
        {
            ClearTab(selected);
        }
    }

    /// <summary>The Dock icon or taskbar button to badge. Set once the main window exists.</summary>
    public void UseBadge(IAppBadge badge)
    {
        _badge = badge;
        _badgeShown = -1;
        UpdateBadge();
    }

    /// <summary>The number of tabs with a waiting prompt, question or plan.</summary>
    public void SetTabsNeedingInput(int count)
    {
        _tabsNeedingInput = count;
        UpdateBadge();
    }

    /// <summary>Settings changed: the badge may have been turned on or off.</summary>
    public void OnSettingsChanged() => UpdateBadge();

    private void UpdateBadge()
    {
        var count = _services.Settings.Notifications.Badge ? _tabsNeedingInput : 0;
        if (count != _badgeShown)
        {
            _badgeShown = count;
            _badge.SetCount(count);
        }
    }

    private void OnNotifierActivated(string id) => _services.Dispatcher.Post(() =>
    {
        if (_shown.Remove(id, out var target))
        {
            Activated?.Invoke(target);
        }
    });

    private static string Id(NotificationKind kind, string? subject) => subject is null ? kind.ToString() : $"{kind}:{subject}";

    public void Dispose()
    {
        _notifier.Activated -= OnNotifierActivated;
        _notifier.Dispose();
    }
}
