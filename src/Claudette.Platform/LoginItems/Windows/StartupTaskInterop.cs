using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Claudette.Platform.Notifications.Windows;
using Claudette.Platform.Updates.Windows;

namespace Claudette.Platform.LoginItems.Windows;

// The WinRT interfaces for the MSIX's startup task and for how the package was activated, declared by hand like the
// ones in WinRtInterop.cs. Method order is the vtable order from windows.applicationmodel.h,
// windows.applicationmodel.activation.h and windows.foundation.h; only the methods up to the last one used are declared.

/// <summary><c>Windows.ApplicationModel.StartupTask</c>'s statics.</summary>
[GeneratedComInterface]
[Guid("EE5B60BD-A148-41A7-B26E-E8B88A1E62F8")]
internal partial interface IStartupTaskStatics : IInspectable
{
    /// <summary>Returns an <c>IAsyncOperation&lt;IVectorView&lt;StartupTask&gt;&gt;</c>. Not used.</summary>
    nint GetForCurrentPackageAsync();

    /// <summary>
    /// Returns an <c>IAsyncOperation&lt;StartupTask&gt;</c>, read through <see cref="IAsyncInfo"/> and
    /// <see cref="IStartupTaskOperation"/>. <paramref name="taskId"/> is an HSTRING.
    /// </summary>
    IAsyncInfo GetAsync(nint taskId);
}

/// <summary>
/// <c>IAsyncOperation&lt;StartupTask&gt;</c>. The IID is the parameterized interface's, as windows.applicationmodel.h
/// declares it.
/// </summary>
[GeneratedComInterface]
[Guid("CBEC7A4E-A046-5330-873D-0FCE228792FA")]
internal partial interface IStartupTaskOperation : IInspectable
{
    void PutCompleted(nint handler);

    nint GetCompleted();

    IStartupTask GetResults();
}

/// <summary><c>Windows.ApplicationModel.StartupTask</c>.</summary>
[GeneratedComInterface]
[Guid("F75C23C8-B5F2-4F6C-88DD-36CB1D599D17")]
internal partial interface IStartupTask : IInspectable
{
    /// <summary>Returns an <c>IAsyncOperation&lt;StartupTaskState&gt;</c>, read through its <see cref="IAsyncInfo"/>.</summary>
    IAsyncInfo RequestEnableAsync();

    void Disable();

    /// <summary><c>StartupTaskState</c>, as <see cref="Claudette.Core.LoginItems.PackageTaskState"/> numbers it.</summary>
    int GetState();
}

/// <summary><c>Windows.ApplicationModel.AppInstance</c>'s statics.</summary>
[GeneratedComInterface]
[Guid("9D11E77F-9EA6-47AF-A6EC-46784C5BA254")]
internal partial interface IAppInstanceStatics : IInspectable
{
    /// <summary>Returns an <c>AppInstance</c>. Not used.</summary>
    nint GetRecommendedInstance();

    IActivatedEventArgs? GetActivatedEventArgs();
}

/// <summary><c>Windows.ApplicationModel.Activation.IActivatedEventArgs</c>.</summary>
[GeneratedComInterface]
[Guid("CF651713-CD08-4FD8-B697-A281B6544E2E")]
internal partial interface IActivatedEventArgs : IInspectable
{
    /// <summary><c>ActivationKind</c>: <see cref="WindowsStartupActivation.StartupTaskKind"/> for a startup task.</summary>
    int GetKind();
}

/// <summary>Whether Windows started the MSIX through its startup task (DESIGN.md §9, "Starting at login").</summary>
public static class WindowsStartupActivation
{
    /// <summary><c>ActivationKind.StartupTask</c>.</summary>
    internal const int StartupTaskKind = 1020;

    /// <summary>
    /// True when this MSIX-installed Claudette was started at login by its startup task, which passes no arguments.
    /// False for any other start, and for a Claudette outside the package.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.17763")]
    public static bool StartedByStartupTask()
    {
        if (!WindowsAppIdentity.IsPackaged)
        {
            return false;
        }
        try
        {
            var statics = WinRt.GetActivationFactory<IAppInstanceStatics>("Windows.ApplicationModel.AppInstance");
            return statics.GetActivatedEventArgs()?.GetKind() == StartupTaskKind;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }
}
