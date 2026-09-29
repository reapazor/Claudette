using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Claudette.Core.Updates;
using Claudette.Platform.Notifications.Windows;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Updates.Windows;

/// <summary>
/// Updates an MSIX-installed Claudette (DESIGN.md §2, "Updating Claudette") the way Microsoft documents for apps
/// published outside the Store: <c>RegisterApplicationRestart</c> with the restart's arguments, then
/// <c>PackageManager.AddPackageAsync</c> with <c>ForceApplicationShutdown</c>. Windows closes Claudette, installs the
/// new version and starts it again with those arguments.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class MsixInstaller(TimeProvider timeProvider, ILogger logger) : IAppInstaller
{
    /// <summary>The application's id in the manifest (packaging/windows/Package.appxmanifest).</summary>
    public const string ApplicationId = "Claudette";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public AppInstallKind Kind => AppInstallKind.Msix;

    public Task<PreparedInstall> PrepareAsync(string packagePath, AppVersion version, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            MsixPackage package;
            try
            {
                package = MsixPackage.Read(packagePath);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
            {
                throw new AppInstallException($"The download isn't a Claudette package: {ex.Message}", inner: ex);
            }
            var family = Deployment.CurrentFamilyName();
            package.CheckUpdates(family, version, RuntimeInformation.ProcessArchitecture);
            return new PreparedInstall(version, packagePath);
        }, cancellationToken);

    public async Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IReadOnlyList<string> restartArguments, string readyFile, string nonce, CancellationToken cancellationToken = default)
    {
        var commandLine = string.Join(' ', restartArguments.Select(Quote));
        var registered = Deployment.RegisterApplicationRestart(commandLine, Deployment.RestartNoCrash | Deployment.RestartNoHang | Deployment.RestartNoReboot);
        if (registered != 0)
        {
            // The next launch still takes the tabs back (RestartSnapshot.UpdateMaxAge); it just won't be automatic.
            logger.LogWarning("RegisterApplicationRestart failed (0x{Result:X8}).", registered);
        }
        int status;
        int error = 0;
        try
        {
            // Stays on the caller's thread: these are the UI thread's WinRT objects, polled between awaits.
            var uris = WinRt.GetActivationFactory<IUriRuntimeClassFactory>("Windows.Foundation.Uri");
            IUriRuntimeClass uri;
            using (var text = new HString(new Uri(prepared.PackagePath).AbsoluteUri))
            {
                uri = uris.CreateUri(text.Handle);
            }
            var manager = (IPackageManager)WinRt.ActivateInstance("Windows.Management.Deployment.PackageManager");
            var operation = manager.AddPackageAsync(uri, 0, Deployment.ForceApplicationShutdown);
            // Normally Windows closes Claudette during this wait.
            while ((status = operation.GetStatus()) == Deployment.AsyncStarted)
            {
                await Task.Delay(PollInterval, timeProvider, cancellationToken);
            }
            if (status != Deployment.AsyncCompleted)
            {
                error = operation.GetErrorCode();
            }
            operation.Close();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Deployment.UnregisterApplicationRestart();
            throw new AppInstallException($"Windows couldn't start the update: {ex.Message}", canOpenPackage: true, ex);
        }
        if (status != Deployment.AsyncCompleted)
        {
            Deployment.UnregisterApplicationRestart();
            throw new AppInstallException(status == Deployment.AsyncCanceled ? "The update was canceled." : MsixPackage.DescribeError(error), canOpenPackage: true);
        }

        // Installed without closing Claudette (no running files were in use): start the new version ourselves.
        Deployment.UnregisterApplicationRestart();
        if (Deployment.CurrentFamilyName() is not { } family)
        {
            throw new AppInstallException("The update is installed. Open Claudette again to use it.");
        }
        try
        {
            Deployment.CreateActivationManager().ActivateApplication($"{family}!{ApplicationId}", commandLine, 0);
        }
        catch (COMException ex)
        {
            throw new AppInstallException($"The update is installed, but the new version couldn't be started: {ex.Message}", inner: ex);
        }
        return InstallOutcome.WaitForNewVersion;
    }

    /// <summary>Quotes an argument for a Windows command line.</summary>
    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : $"\"{argument.Replace("\"", "\\\"")}\"";
}
