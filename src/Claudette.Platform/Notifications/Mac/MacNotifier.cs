using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Notifications.Mac;

/// <summary>
/// macOS User Notifications (DESIGN.md §10) through <c>UNUserNotificationCenter</c>. It only works in an app bundle
/// with a bundle identifier: run unbundled (for example with <c>dotnet run</c>), macOS refuses, so this reports itself
/// unavailable instead. A delegate object, built at runtime, reports clicks and lets notifications show while Claudette
/// is in front (the notification service already skips the ones about the tab being looked at).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacNotifier : INotifier
{
    private const string UserNotificationsFramework = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";

    /// <summary>UNAuthorizationOptionBadge | Sound | Alert.</summary>
    private const nuint AuthorizationOptions = 7;

    // UNNotificationPresentationOptions.
    private const nuint PresentSound = 1 << 1;
    private const nuint PresentAlert = 1 << 2;
    private const nuint PresentList = 1 << 3;
    private const nuint PresentBanner = 1 << 4;

    /// <summary>The one notifier; the delegate's callbacks are static and find it here.</summary>
    private static MacNotifier? s_current;
    private static nint s_delegate;

    private readonly ILogger _logger;
    private readonly nint _center;

    public MacNotifier(ILogger logger)
    {
        _logger = logger;
        var bundleId = ObjC.ToManagedString(ObjC.Send(ObjC.Send(ObjC.GetClass("NSBundle"), "mainBundle"), "bundleIdentifier"));
        if (string.IsNullOrEmpty(bundleId) || !NativeLibrary.TryLoad(UserNotificationsFramework, out _))
        {
            logger.LogInformation("Not running from an app bundle, so macOS notifications are off.");
            return;
        }
        _center = ObjC.Send(ObjC.GetClass("UNUserNotificationCenter"), "currentNotificationCenter");
        s_current = this;
        ObjC.Send(_center, "setDelegate:", CreateDelegate());
        ObjC.Send(_center, "requestAuthorizationWithOptions:completionHandler:", AuthorizationOptions,
            ObjC.GlobalBlock((nint)(delegate* unmanaged<nint, byte, nint, void>)&OnAuthorized, "v@?B@"));
    }

    public bool IsAvailable => _center != 0;

    public event Action<string>? Activated;

    public void Show(OsNotification notification)
    {
        if (_center == 0)
        {
            return;
        }
        var pool = ObjC.AutoreleasePoolPush();
        var content = ObjC.New("UNMutableNotificationContent");
        var title = ObjC.NSString(notification.Title);
        var body = ObjC.NSString(notification.Body);
        var id = ObjC.NSString(notification.Id);
        try
        {
            ObjC.Send(content, "setTitle:", title);
            ObjC.Send(content, "setBody:", body);
            ObjC.Send(content, "setSound:", ObjC.Send(ObjC.GetClass("UNNotificationSound"), "defaultSound"));
            // Same identifier: macOS replaces the earlier notification.
            var request = ObjC.Send(ObjC.GetClass("UNNotificationRequest"), "requestWithIdentifier:content:trigger:", id, content, 0);
            ObjC.Send(_center, "addNotificationRequest:withCompletionHandler:", request, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't show a notification.");
        }
        finally
        {
            ObjC.Release(id);
            ObjC.Release(body);
            ObjC.Release(title);
            ObjC.Release(content);
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    public void Remove(string id)
    {
        if (_center == 0)
        {
            return;
        }
        var pool = ObjC.AutoreleasePoolPush();
        var nsId = ObjC.NSString(id);
        try
        {
            var ids = ObjC.Send(ObjC.GetClass("NSArray"), "arrayWithObject:", nsId);
            ObjC.Send(_center, "removeDeliveredNotificationsWithIdentifiers:", ids);
            ObjC.Send(_center, "removePendingNotificationRequestsWithIdentifiers:", ids);
        }
        finally
        {
            ObjC.Release(nsId);
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    /// <summary>An NSObject subclass implementing the two UNUserNotificationCenterDelegate methods Claudette needs.</summary>
    private static nint CreateDelegate()
    {
        if (s_delegate != 0)
        {
            return s_delegate;
        }
        var cls = ObjC.GetClass("ClaudetteNotificationDelegate");
        if (cls == 0)
        {
            cls = ObjC.AllocateClassPair(ObjC.GetClass("NSObject"), "ClaudetteNotificationDelegate", 0);
            ObjC.AddMethod(cls, ObjC.Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidReceiveResponse, "v@:@@@?");
            ObjC.AddMethod(cls, ObjC.Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@?");
            if (ObjC.GetProtocol("UNUserNotificationCenterDelegate") is var protocol and not 0)
            {
                ObjC.AddProtocol(cls, protocol);
            }
            ObjC.RegisterClassPair(cls);
        }
        // The center holds its delegate weakly; this instance is kept for the life of the process.
        s_delegate = ObjC.Send(ObjC.Send(cls, "alloc"), "init");
        return s_delegate;
    }

    [UnmanagedCallersOnly]
    private static void OnAuthorized(nint block, byte granted, nint error)
    {
        // Nothing to do: without permission macOS simply doesn't show them.
    }

    [UnmanagedCallersOnly]
    private static void DidReceiveResponse(nint self, nint selector, nint center, nint response, nint completionHandler)
    {
        try
        {
            var request = ObjC.Send(ObjC.Send(response, "notification"), "request");
            if (ObjC.ToManagedString(ObjC.Send(request, "identifier")) is { } id && s_current is { } notifier)
            {
                notifier.Activated?.Invoke(id);
            }
        }
        catch
        {
            // Never let an exception cross back into Objective-C.
        }
        finally
        {
            ObjC.CallBlock(completionHandler);
        }
    }

    [UnmanagedCallersOnly]
    private static void WillPresent(nint self, nint selector, nint center, nint notification, nint completionHandler)
    {
        var options = OperatingSystem.IsMacOSVersionAtLeast(11) ? PresentBanner | PresentList | PresentSound : PresentAlert | PresentSound;
        ObjC.CallBlock(completionHandler, options);
    }
}

/// <summary>
/// The Dock icon (DESIGN.md §10): the tile's badge with the number of tabs needing input, and the whole icon replaced by
/// each frame of an animation through <c>NSApplication.applicationIconImage</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacDockBadge(ILogger logger) : IAppBadge
{
    /// <summary>An animation's few frames are shown over and over, so each is made into an NSImage once, and kept.</summary>
    private readonly Dictionary<byte[], nint> _images = new(ReferenceEqualityComparer.Instance);

    private int _count;

    public AppIconSurface Surface => AppIconSurface.Icon;

    public void ShowFrame(byte[]? png, string? description)
    {
        try
        {
            // nil gives the Dock back the app's own icon.
            var application = ObjC.Send(ObjC.GetClass("NSApplication"), "sharedApplication");
            ObjC.Send(application, "setApplicationIconImage:", png is null ? 0 : Image(png));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't set the Dock icon.");
        }
    }

    /// <summary>The Dock has no flash; the badge and the waving ask for attention.</summary>
    public void Flash(bool on)
    {
    }

    private nint Image(byte[] png)
    {
        if (_images.TryGetValue(png, out var image))
        {
            return image;
        }
        fixed (byte* bytes = png)
        {
            var data = ObjC.Send(ObjC.Send(ObjC.GetClass("NSData"), "alloc"), "initWithBytes:length:", (nint)bytes, png.Length);
            try
            {
                image = ObjC.Send(ObjC.Send(ObjC.GetClass("NSImage"), "alloc"), "initWithData:", data);
            }
            finally
            {
                ObjC.Release(data);
            }
        }
        _images[png] = image;
        return image;
    }

    public void SetCount(int count)
    {
        if (count == _count)
        {
            return;
        }
        var label = count > 0 ? ObjC.NSString(count.ToString(System.Globalization.CultureInfo.InvariantCulture)) : 0;
        try
        {
            var dockTile = ObjC.Send(ObjC.Send(ObjC.GetClass("NSApplication"), "sharedApplication"), "dockTile");
            ObjC.Send(dockTile, "setBadgeLabel:", label);
            _count = count;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't set the Dock badge.");
        }
        finally
        {
            ObjC.Release(label);
        }
    }
}
