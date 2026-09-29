using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Claudette.Platform.Notifications.Windows;

namespace Claudette.Platform.Updates.Windows;

// The WinRT and shell interfaces an MSIX self-update needs, declared by hand like the ones in WinRtInterop.cs. Method
// order is the vtable order from windows.management.deployment.h, windows.foundation.h, asyncinfo.h and
// shobjidl_core.h; only the methods up to the last one used are declared.

/// <summary><c>Windows.Foundation.Uri</c>'s factory.</summary>
[GeneratedComInterface]
[Guid("44A9796F-723E-4FDF-A218-033E75B0C084")]
internal partial interface IUriRuntimeClassFactory : IInspectable
{
    IUriRuntimeClass CreateUri(nint uri);
}

/// <summary><c>Windows.Foundation.Uri</c>, only passed along.</summary>
[GeneratedComInterface]
[Guid("9E365E57-48B2-4160-956F-C7385120BBFC")]
internal partial interface IUriRuntimeClass : IInspectable
{
}

/// <summary><c>Windows.Management.Deployment.PackageManager</c>.</summary>
[GeneratedComInterface]
[Guid("9A7D4B65-5E8F-4FC7-A2E5-7F6925CB8B53")]
internal partial interface IPackageManager : IInspectable
{
    /// <summary>
    /// Returns an <c>IAsyncOperationWithProgress&lt;DeploymentResult, DeploymentProgress&gt;</c>, read through its
    /// <c>IAsyncInfo</c>. <paramref name="dependencyPackageUris"/> is an <c>IIterable&lt;Uri&gt;</c>, or 0 for none.
    /// </summary>
    IAsyncInfo AddPackageAsync(IUriRuntimeClass packageUri, nint dependencyPackageUris, uint deploymentOptions);
}

/// <summary>What every WinRT async operation implements: its status and error, without the generic result type.</summary>
[GeneratedComInterface]
[Guid("00000036-0000-0000-C000-000000000046")]
internal partial interface IAsyncInfo : IInspectable
{
    uint GetId();

    /// <summary><c>AsyncStatus</c>: Started 0, Completed 1, Canceled 2, Error 3.</summary>
    int GetStatus();

    int GetErrorCode();

    void Cancel();

    void Close();
}

/// <summary>Starts a packaged app with arguments, as the Start menu does.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
internal partial interface IApplicationActivationManager
{
    /// <summary>Returns the new process's id.</summary>
    uint ActivateApplication(string appUserModelId, string? arguments, int options);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class Deployment
{
    /// <summary><c>DeploymentOptions.ForceApplicationShutdown</c>: Windows closes the running app to update it.</summary>
    public const uint ForceApplicationShutdown = 1;

    public const int AsyncStarted = 0;
    public const int AsyncCompleted = 1;
    public const int AsyncCanceled = 2;

    // RegisterApplicationRestart flags: restart only after an update, not after a crash, a hang or a reboot.
    public const uint RestartNoCrash = 1;
    public const uint RestartNoHang = 2;
    public const uint RestartNoReboot = 8;

    public static readonly Guid ApplicationActivationManagerClsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    private const int ClsctxLocalServer = 4;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegisterApplicationRestart(string? commandLine, uint flags);

    [LibraryImport("kernel32.dll")]
    public static partial int UnregisterApplicationRestart();

    [LibraryImport("kernel32.dll")]
    public static partial int GetCurrentPackageFamilyName(ref uint length, char* packageFamilyName);

    /// <summary>This process's package family name, such as <c>reapazor.Claudette_1a2b3c4d5e6f7</c>, or null unpackaged.</summary>
    public static string? CurrentFamilyName()
    {
        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) == WinRt.AppModelErrorNoPackage || length == 0)
        {
            return null;
        }
        var buffer = new char[length];
        fixed (char* chars = buffer)
        {
            return GetCurrentPackageFamilyName(ref length, chars) == 0 ? new string(chars, 0, (int)length - 1) : null;
        }
    }

    private const int ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, nint packageFullNames, ref uint bufferLength, nint buffer);

    /// <summary>Whether a package of <paramref name="familyName"/> is installed for this user, from inside a package or not.</summary>
    public static bool IsFamilyInstalled(string familyName)
    {
        uint count = 0;
        uint length = 0;
        var result = GetPackagesByPackageFamily(familyName, ref count, 0, ref length, 0);
        return result is 0 or ErrorInsufficientBuffer && count > 0;
    }

    public static IApplicationActivationManager CreateActivationManager()
    {
        Marshal.ThrowExceptionForHR(WinRt.CoCreateInstance(ApplicationActivationManagerClsid, 0, ClsctxLocalServer, typeof(IApplicationActivationManager).GUID, out var instance));
        try
        {
            return ComInterfaceMarshaller<IApplicationActivationManager>.ConvertToManaged((void*)instance)!;
        }
        finally
        {
            Marshal.Release(instance);
        }
    }
}
