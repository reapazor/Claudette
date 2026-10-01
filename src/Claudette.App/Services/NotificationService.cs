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
    /// <summary>A long project action, such as a build, finished or failed (DESIGN.md §18, "Project tools").</summary>
    ProjectAction,
}

/// <summary>Where clicking a notification goes: a tab, or an app-wide screen.</summary>
public sealed record NotificationTarget(NotificationKind Kind, string? TabId);

/// <summary>
/// Decides which OS notifications to send and routes clicks back (DESIGN.md §10), and keeps the Dock icon or taskbar
/// button: its badge, its animation and its flash. A notification is skipped when its type is off, or when Claudette is
/// in front and already showing what it's about: the tab it's about is selected, or, for app-wide ones, the header or
/// screen that already says it. Use on the UI thread.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly AppServices _services;
    private readonly INotifier _notifier;
    private readonly Dictionary<string, NotificationTarget> _shown = new(StringComparer.Ordinal);
    private IAppBadge _badge = NullNotifier.Instance;
    private int _tabsNeedingInput;
    private int _tabsLimited;
    private int _tabsWorking;
    private int _badgeShown = -1;
    private bool _flashing;
    private AppIconAnimation? _animation;
    private int _frame;
    private ITimer? _frameTimer;

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
        // A project action's end is news whichever tab is showing, until Claudette is in front: the sidebar already says it.
        if (IsAppActive && (tabId is null || tabId == SelectedTabId() || kind == NotificationKind.ProjectAction))
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
            NotificationKind.ProjectAction => settings.ProjectActions,
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

    /// <summary>
    /// Claudette's window came to the front or went to the back. The presence file follows it, so Remote Control pushes
    /// to the phone only while the user is away from Claudette (DESIGN.md §10).
    /// </summary>
    public void SetAppActive(bool active)
    {
        IsAppActive = active;
        _services.RemoteControl.SetAppActive(active);
        if (active)
        {
            SetFlashing(false);
            if (SelectedTabId() is { } selected)
            {
                ClearTab(selected);
            }
        }
    }

    /// <summary>The Dock icon or taskbar button to badge and animate. Set once the main window exists.</summary>
    public void UseBadge(IAppBadge badge)
    {
        Animate(null);
        _badge = badge;
        _badgeShown = -1;
        UpdateBadge();
    }

    /// <summary>
    /// The number of tabs with a waiting prompt, question or plan, of tabs a usage limit stopped that wait for it to
    /// reset, and of tabs working. When more tabs need input while Claudette isn't in front, the taskbar button flashes,
    /// if that notification is on.
    /// </summary>
    public void SetTabActivity(int needingInput, int limited, int working)
    {
        if (needingInput > _tabsNeedingInput && !IsAppActive && IsEnabled(NotificationKind.NeedsInput))
        {
            SetFlashing(true);
        }
        else if (needingInput == 0)
        {
            SetFlashing(false);
        }
        _tabsNeedingInput = needingInput;
        _tabsLimited = limited;
        _tabsWorking = working;
        UpdateBadge();
    }

    /// <summary>Settings changed: the badge or the animation may have been turned on or off.</summary>
    public void OnSettingsChanged() => UpdateBadge();

    private void UpdateBadge()
    {
        var settings = _services.Settings.Notifications;
        var count = settings.Badge ? _tabsNeedingInput : 0;
        if (count != _badgeShown)
        {
            _badgeShown = count;
            _badge.SetCount(count);
        }
        // A tab needing input comes first. Then a usage limit, over other tabs working: it stopped a task, and the
        // rest are likely to run into it too.
        Animate(!settings.AnimateIcon ? null : _badge.Surface switch
        {
            AppIconSurface.Overlay when count > 0 => null,
            AppIconSurface.Overlay => _tabsLimited > 0 ? AppIconAnimations.Hourglass : _tabsWorking > 0 ? AppIconAnimations.Spark : null,
            AppIconSurface.Icon => _tabsNeedingInput > 0 ? AppIconAnimations.Waving
                : _tabsLimited > 0 ? AppIconAnimations.Waiting
                : _tabsWorking > 0 ? AppIconAnimations.Typing : null,
            _ => null,
        });
    }

    private void SetFlashing(bool on)
    {
        // Flashing again restarts it, for another tab needing input.
        if (on || _flashing)
        {
            _flashing = on;
            _badge.Flash(on);
        }
    }

    // ---- The icon's animation -----------------------------------------------------------------------------------

    private void Animate(AppIconAnimation? animation)
    {
        if (ReferenceEquals(animation, _animation))
        {
            return;
        }
        _frameTimer?.Dispose();
        _frameTimer = null;
        _animation = animation;
        _frame = 0;
        if (animation is null)
        {
            _badge.ShowFrame(null, null);
            return;
        }
        // One timer per animation, so a tick queued before it changed does nothing.
        _frameTimer = _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() => NextFrame(animation)), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ShowFrame();
    }

    private void NextFrame(AppIconAnimation animation)
    {
        if (!ReferenceEquals(animation, _animation))
        {
            return;
        }
        _frame = (_frame + 1) % animation.Frames.Count;
        ShowFrame();
    }

    private void ShowFrame()
    {
        if (_animation is not { } animation)
        {
            return;
        }
        var frame = animation.Frames[_frame];
        _badge.ShowFrame(frame.Png, animation.Description);
        _frameTimer?.Change(frame.Duration, Timeout.InfiniteTimeSpan);
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
        _frameTimer?.Dispose();
        _frameTimer = null;
        _notifier.Activated -= OnNotifierActivated;
        _notifier.Dispose();
    }
}
