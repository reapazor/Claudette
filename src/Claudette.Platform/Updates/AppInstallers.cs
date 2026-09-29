using Claudette.Core.Processes;
using Claudette.Core.Updates;
using Claudette.Platform.Notifications.Windows;
using Claudette.Platform.Updates.Mac;
using Claudette.Platform.Updates.Windows;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Updates;

public static class AppInstallers
{
    /// <summary>
    /// The installer for how this Claudette was installed (DESIGN.md §2, "Updating Claudette"): the MSIX package on
    /// Windows, or <c>Claudette.app</c> on macOS. Anything else, such as a Linux build or an unpackaged Windows one,
    /// can't install updates itself, and is offered the release page instead.
    /// </summary>
    /// <param name="updatesDirectory">Where downloads go, and on macOS where the image is mounted.</param>
    public static IAppInstaller CreateForCurrentOS(IProcessLauncher launcher, TimeProvider timeProvider, string updatesDirectory, ILogger logger)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && WindowsAppIdentity.IsPackaged)
        {
            return new MsixInstaller(timeProvider, logger);
        }
        if (OperatingSystem.IsMacOS() && MacAppInstaller.FindBundle(AppContext.BaseDirectory) is { } bundle)
        {
            return new MacAppInstaller(launcher, timeProvider, bundle, updatesDirectory, Environment.ProcessId);
        }
        return new NoAppInstaller();
    }
}
