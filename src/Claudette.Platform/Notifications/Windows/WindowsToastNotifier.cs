using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Claudette.Platform.Notifications.Windows;

/// <summary>
/// Claudette's identity on Windows, which toasts, taskbar grouping and the jump list hang off. An MSIX install has
/// package identity and needs nothing more. Run unpackaged (development), Claudette sets an explicit AppUserModelID for
/// its process and registers it under <c>HKCU\Software\Classes\AppUserModelId</c>, as the Windows App SDK does, so
/// toasts can name it.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsAppIdentity
{
    /// <summary>Kept in step with the MSIX manifest's identity (packaging/windows).</summary>
    public const string AppUserModelId = "MatthewDavey.Claudette";

    /// <summary>True when running from an MSIX package.</summary>
    public static bool IsPackaged { get; } = HasPackageIdentity();

    /// <summary>Call once at startup, before the first window is created.</summary>
    /// <param name="iconPath">An icon file for toasts sent by an unpackaged Claudette, or null.</param>
    public static void Initialize(string? iconPath)
    {
        if (IsPackaged)
        {
            return;
        }
        WinRt.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
        key.SetValue("DisplayName", "Claudette");
        if (iconPath is not null)
        {
            key.SetValue("IconUri", iconPath);
        }
    }

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        return WinRt.GetCurrentPackageFullName(ref length, null) != WinRt.AppModelErrorNoPackage;
    }
}

/// <summary>
/// Windows toasts (DESIGN.md §10) through the WinRT <c>ToastNotificationManager</c>. Clicking a toast raises its
/// <c>Activated</c> event in this process, which reports the notification's id. Call <see cref="Show"/> and
/// <see cref="Remove"/> on the UI thread; <see cref="Activated"/> is raised on a background thread.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class WindowsToastNotifier : INotifier
{
    private readonly ILogger _logger;
    private readonly IToastNotifier _notifier;
    private readonly IToastNotificationFactory _factory;
    private readonly Dictionary<string, (IToastNotification Toast, long Token)> _shown = new(StringComparer.Ordinal);

    public WindowsToastNotifier(ILogger logger)
    {
        _logger = logger;
        var manager = WinRt.GetActivationFactory<IToastNotificationManagerStatics>("Windows.UI.Notifications.ToastNotificationManager");
        if (WindowsAppIdentity.IsPackaged)
        {
            _notifier = manager.CreateToastNotifier();
        }
        else
        {
            using var id = new HString(WindowsAppIdentity.AppUserModelId);
            _notifier = manager.CreateToastNotifierWithId(id.Handle);
        }
        _factory = WinRt.GetActivationFactory<IToastNotificationFactory>("Windows.UI.Notifications.ToastNotification");
    }

    public bool IsAvailable => true;

    public event Action<string>? Activated;

    public void Show(OsNotification notification)
    {
        Remove(notification.Id);
        try
        {
            var toast = Create(notification);
            var token = toast.AddActivated(new ToastActivatedHandler(() => Activated?.Invoke(notification.Id)));
            _notifier.Show(toast);
            _shown[notification.Id] = (toast, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't show a toast.");
        }
    }

    /// <summary>Builds the toast without showing it.</summary>
    internal IToastNotification Create(OsNotification notification)
    {
        var document = WinRt.ActivateInstance("Windows.Data.Xml.Dom.XmlDocument");
        using (var xml = new HString(ToastContent.Xml(notification)))
        {
            ((IXmlDocumentIO)document).LoadXml(xml.Handle);
        }
        return _factory.CreateToastNotification((IXmlDocument)document);
    }

    public void Remove(string id)
    {
        if (!_shown.Remove(id, out var shown))
        {
            return;
        }
        try
        {
            shown.Toast.RemoveActivated(shown.Token);
            _notifier.Hide(shown.Toast);
        }
        catch (Exception ex)
        {
            // Already gone, for example dismissed by the user.
            _logger.LogDebug(ex, "Couldn't hide a toast.");
        }
    }

    public void Dispose()
    {
        foreach (var id in _shown.Keys.ToArray())
        {
            Remove(id);
        }
    }
}

/// <summary>The toast's XML: the generic template with a title line and a body.</summary>
public static class ToastContent
{
    public static string Xml(OsNotification notification) =>
        $"""<toast launch="{SecurityElement.Escape(notification.Id)}"><visual><binding template="ToastGeneric"><text>{SecurityElement.Escape(notification.Title)}</text><text>{SecurityElement.Escape(notification.Body)}</text></binding></visual></toast>""";
}
