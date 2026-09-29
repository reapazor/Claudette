using Claudette.Core.Processes;
using Claudette.Platform.Notifications.Linux;
using Claudette.Platform.Notifications.Mac;
using Claudette.Platform.Notifications.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Notifications;

/// <summary>One OS notification (DESIGN.md §10).</summary>
/// <param name="Id">
/// Identifies it: a newer notification with the same id replaces it, <see cref="INotifier.Remove"/> takes it away, and
/// clicking it reports this id.
/// </param>
public sealed record OsNotification(string Id, string Title, string Body);

/// <summary>Shows native OS notifications: Windows toasts, macOS User Notifications, freedesktop notifications on Linux.</summary>
public interface INotifier : IDisposable
{
    /// <summary>
    /// False when notifications can't be shown here, for example a macOS build that isn't running from its <c>.app</c>
    /// bundle. Showing one is then a no-op.
    /// </summary>
    bool IsAvailable { get; }

    void Show(OsNotification notification);

    /// <summary>Takes a notification away, for example once its tab has been looked at.</summary>
    void Remove(string id);

    /// <summary>Raised with the notification's id when the user clicks it. May be raised on any thread.</summary>
    event Action<string>? Activated;
}

/// <summary>Where an OS shows the icon's animation (DESIGN.md §10).</summary>
public enum AppIconSurface
{
    /// <summary>Nowhere: Linux, and tests.</summary>
    None,

    /// <summary>The taskbar button's overlay (Windows), a small image it shares with the count.</summary>
    Overlay,

    /// <summary>The whole Dock icon (macOS); the count is a label over it.</summary>
    Icon,
}

/// <summary>
/// The Dock icon (macOS) or taskbar button (Windows), DESIGN.md §10: the number of tabs needing input, a frame of an
/// animation while tabs work or wait, and a flash to ask for attention. Call it on the UI thread.
/// </summary>
public interface IAppBadge
{
    /// <summary>Where this OS shows <see cref="ShowFrame"/>.</summary>
    AppIconSurface Surface { get; }

    /// <summary>Shows <paramref name="count"/>; 0 clears it.</summary>
    void SetCount(int count);

    /// <summary>
    /// Shows one frame of an animation, a PNG for <see cref="Surface"/>, or none: the overlay goes, or the Dock icon is
    /// Claudette's own again. On Windows a count, while there is one, stays in front of it.
    /// </summary>
    /// <param name="description">What it means, for screen readers (Windows).</param>
    void ShowFrame(byte[]? png, string? description);

    /// <summary>Flashes the taskbar button until Claudette comes to the front (Windows); false stops it.</summary>
    void Flash(bool on);
}

/// <summary>Nothing to show on: tests, and platforms without notifications.</summary>
public sealed class NullNotifier : INotifier, IAppBadge
{
    public static NullNotifier Instance { get; } = new();

    public bool IsAvailable => false;

    public AppIconSurface Surface => AppIconSurface.None;

    public event Action<string>? Activated
    {
        add { }
        remove { }
    }

    public void Show(OsNotification notification)
    {
    }

    public void Remove(string id)
    {
    }

    public void SetCount(int count)
    {
    }

    public void ShowFrame(byte[]? png, string? description)
    {
    }

    public void Flash(bool on)
    {
    }

    public void Dispose()
    {
    }
}

public static class Notifier
{
    /// <summary>
    /// The notifier for the OS Claudette is running on. Anything that fails to set up falls back to
    /// <see cref="NullNotifier"/>: notifications are never worth failing to start over.
    /// </summary>
    /// <param name="launcher">Runs <c>notify-send</c> on Linux.</param>
    public static INotifier CreateForCurrentOS(IProcessLauncher launcher, ILoggerFactory? loggerFactory = null)
    {
        var loggers = loggerFactory ?? NullLoggerFactory.Instance;
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            {
                return new WindowsToastNotifier(loggers.CreateLogger<WindowsToastNotifier>());
            }
            if (OperatingSystem.IsMacOS())
            {
                return new MacNotifier(loggers.CreateLogger<MacNotifier>());
            }
            if (OperatingSystem.IsLinux())
            {
                return NotifySendNotifier.TryCreate(launcher, loggers.CreateLogger<NotifySendNotifier>()) ?? (INotifier)NullNotifier.Instance;
            }
        }
        catch (Exception ex)
        {
            loggers.CreateLogger(typeof(Notifier)).LogWarning(ex, "OS notifications aren't available.");
        }
        return NullNotifier.Instance;
    }

    /// <summary>The Dock icon or taskbar button for this OS; nothing on Linux.</summary>
    /// <param name="windowHandle">The main window's native handle (Windows).</param>
    /// <param name="renderIcon">Draws the count as a small PNG icon (Windows overlay icons are images).</param>
    public static IAppBadge CreateBadgeForCurrentOS(Func<nint> windowHandle, Func<int, byte[]?> renderIcon, ILoggerFactory? loggerFactory = null)
    {
        var loggers = loggerFactory ?? NullLoggerFactory.Instance;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsTaskbarBadge(windowHandle, renderIcon, loggers.CreateLogger<WindowsTaskbarBadge>());
            }
            if (OperatingSystem.IsMacOS())
            {
                return new MacDockBadge(loggers.CreateLogger<MacDockBadge>());
            }
        }
        catch (Exception ex)
        {
            loggers.CreateLogger(typeof(Notifier)).LogWarning(ex, "The Dock or taskbar badge isn't available.");
        }
        return NullNotifier.Instance;
    }
}
