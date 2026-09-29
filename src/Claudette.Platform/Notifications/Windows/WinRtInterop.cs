using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Claudette.Platform.Notifications.Windows;

// The few WinRT and shell interfaces Claudette needs, declared by hand so the Platform project stays a plain net10.0
// library (no Windows-only target framework or projection package). Method order is the vtable order from the Windows
// SDK headers (windows.ui.notifications.h, windows.data.xml.dom.h, shobjidl_core.h); each WinRT interface starts with
// the three IInspectable methods. HSTRINGs are passed as nint (see HString).

[GeneratedComInterface]
[Guid("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90")]
internal partial interface IInspectable
{
    void GetIids(out uint iidCount, out nint iids);

    void GetRuntimeClassName(out nint className);

    void GetTrustLevel(out int trustLevel);
}

/// <summary><c>Windows.UI.Notifications.ToastNotificationManager</c>'s statics.</summary>
[GeneratedComInterface]
[Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4")]
internal partial interface IToastNotificationManagerStatics : IInspectable
{
    IToastNotifier CreateToastNotifier();

    IToastNotifier CreateToastNotifierWithId(nint applicationId);
}

[GeneratedComInterface]
[Guid("75927B93-03F3-41EC-91D3-6E5BAC1B38E7")]
internal partial interface IToastNotifier : IInspectable
{
    void Show(IToastNotification notification);

    void Hide(IToastNotification notification);
}

[GeneratedComInterface]
[Guid("997E2675-059E-4E60-8B06-1760917C8B80")]
internal partial interface IToastNotification : IInspectable
{
    nint GetContent();

    void PutExpirationTime(nint value);

    nint GetExpirationTime();

    long AddDismissed(nint handler);

    void RemoveDismissed(long token);

    /// <summary>Takes a <c>TypedEventHandler&lt;ToastNotification, object&gt;</c>; returns the registration token.</summary>
    long AddActivated(IToastActivatedHandler handler);

    void RemoveActivated(long token);
}

/// <summary><c>Windows.UI.Notifications.ToastNotification</c>'s factory.</summary>
[GeneratedComInterface]
[Guid("04124B20-82C6-4229-B109-FD9ED4662B53")]
internal partial interface IToastNotificationFactory : IInspectable
{
    IToastNotification CreateToastNotification(IXmlDocument content);
}

/// <summary><c>Windows.Data.Xml.Dom.XmlDocument</c>, only passed along.</summary>
[GeneratedComInterface]
[Guid("F7F3A506-1E87-42D6-BCFB-B8C809FA5494")]
internal partial interface IXmlDocument : IInspectable
{
}

[GeneratedComInterface]
[Guid("6CD0E74E-EE65-4489-9EBF-CA43E87BA637")]
internal partial interface IXmlDocumentIO : IInspectable
{
    void LoadXml(nint xml);
}

/// <summary>
/// <c>TypedEventHandler&lt;ToastNotification, object&gt;</c>. WinRT delegates derive from IUnknown, not IInspectable;
/// the IID is the parameterized interface's.
/// </summary>
[GeneratedComInterface]
[Guid("AB54DE2D-97D9-5528-B6AD-105AFE156530")]
internal partial interface IToastActivatedHandler
{
    void Invoke(nint sender, nint args);
}

[GeneratedComClass]
internal sealed partial class ToastActivatedHandler(Action onActivated) : IToastActivatedHandler
{
    public void Invoke(nint sender, nint args) => onActivated();
}

/// <summary><c>ITaskbarList3</c>, flattened with <c>ITaskbarList</c> and <c>ITaskbarList2</c> before it.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
internal partial interface ITaskbarList3
{
    void HrInit();

    void AddTab(nint hwnd);

    void DeleteTab(nint hwnd);

    void ActivateTab(nint hwnd);

    void SetActiveAlt(nint hwnd);

    void MarkFullscreenWindow(nint hwnd, int fullscreen);

    void SetProgressValue(nint hwnd, ulong completed, ulong total);

    void SetProgressState(nint hwnd, int flags);

    void RegisterTab(nint tab, nint mdi);

    void UnregisterTab(nint tab);

    void SetTabOrder(nint tab, nint insertBefore);

    void SetTabActive(nint tab, nint mdi, uint reserved);

    void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);

    void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);

    void ThumbBarSetImageList(nint hwnd, nint imageList);

    void SetOverlayIcon(nint hwnd, nint icon, string? description);
}

/// <summary>FLASHWINFO, for <see cref="WinRt.FlashWindowEx"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FlashWindowInfo
{
    public uint Size;
    public nint Window;
    public uint Flags;
    public uint Count;
    public uint Timeout;
}

/// <summary>An HSTRING, deleted on dispose.</summary>
[SupportedOSPlatform("windows")]
internal readonly unsafe struct HString : IDisposable
{
    public HString(string value)
    {
        fixed (char* chars = value)
        {
            Marshal.ThrowExceptionForHR(WinRt.WindowsCreateString(chars, (uint)value.Length, out var handle));
            Handle = handle;
        }
    }

    public nint Handle { get; }

    public void Dispose()
    {
        if (Handle != 0)
        {
            WinRt.WindowsDeleteString(Handle);
        }
    }
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class WinRt
{
    public const int ClsctxInprocServer = 1;

    /// <summary>GetCurrentPackageFullName's answer for a process without package identity.</summary>
    public const int AppModelErrorNoPackage = 15700;

    [LibraryImport("combase.dll")]
    public static partial int WindowsCreateString(char* sourceString, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll")]
    public static partial int RoActivateInstance(nint activatableClassId, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, int context, in Guid iid, out nint instance);

    [LibraryImport("kernel32.dll")]
    public static partial int GetCurrentPackageFullName(ref uint length, char* packageFullName);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint CreateIconFromResourceEx(byte* resource, uint size, [MarshalAs(UnmanagedType.Bool)] bool isIcon, uint version, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    /// <summary>FLASHW_STOP: back to normal.</summary>
    public const uint FlashStop = 0;

    /// <summary>FLASHW_TRAY: the taskbar button.</summary>
    public const uint FlashTray = 2;

    /// <summary>FLASHW_TIMERNOFG: until the window comes to the foreground.</summary>
    public const uint FlashUntilForeground = 12;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlashWindowEx(FlashWindowInfo* info);

    /// <summary>The activation factory of a WinRT class, as <typeparamref name="T"/>.</summary>
    public static T GetActivationFactory<T>(string className)
    {
        using var name = new HString(className);
        Marshal.ThrowExceptionForHR(RoGetActivationFactory(name.Handle, typeof(T).GUID, out var factory));
        return Wrap<T>(factory);
    }

    /// <summary>A new instance of a WinRT class with a default constructor.</summary>
    public static IInspectable ActivateInstance(string className)
    {
        using var name = new HString(className);
        Marshal.ThrowExceptionForHR(RoActivateInstance(name.Handle, out var instance));
        return Wrap<IInspectable>(instance);
    }

    public static T CreateComInstance<T>(Guid clsid)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, ClsctxInprocServer, typeof(T).GUID, out var instance));
        return Wrap<T>(instance);
    }

    /// <summary>Wraps a native interface pointer and releases the reference it came with.</summary>
    private static T Wrap<T>(nint pointer)
    {
        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer)!;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
