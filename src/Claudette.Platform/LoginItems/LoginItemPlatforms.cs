using Claudette.Core.LoginItems;
using Claudette.Core.Processes;
using Claudette.Core.Updates;
using Claudette.Platform.LoginItems.Linux;
using Claudette.Platform.LoginItems.Mac;
using Claudette.Platform.LoginItems.Windows;
using Claudette.Platform.Notifications.Windows;
using Claudette.Platform.Updates.Mac;
using Claudette.Platform.Updates.Windows;

namespace Claudette.Platform.LoginItems;

public static class LoginItemPlatforms
{
    /// <summary>
    /// How this OS starts Claudette at login (DESIGN.md §9, "Starting at login"): the Run key and the MSIX's startup task
    /// on Windows, a LaunchAgent on macOS, an XDG autostart file on Linux.
    /// </summary>
    public static ILoginItems CreateForCurrentOS(IProcessLauncher launcher, TimeProvider timeProvider)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return new WindowsLoginItems(launcher, timeProvider);
        }
        if (OperatingSystem.IsMacOS())
        {
            return new LaunchAgentLoginItems(launcher, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"));
        }
        if (OperatingSystem.IsLinux())
        {
            return new XdgAutostartLoginItems(launcher, XdgAutostartLoginItems.DefaultDirectory());
        }
        return new NoLoginItems("Claudette can't start at login on this OS.");
    }

    /// <summary>
    /// This Claudette as it's installed: the MSIX by its package family, <c>Claudette.app</c> by its bundle, and anything
    /// else by its folder. A source build isn't told apart here: it starts from its build output, which the caller knows.
    /// </summary>
    public static ClaudetteCopy CurrentCopy(string version)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && WindowsAppIdentity.IsPackaged && Deployment.CurrentFamilyName() is { } family)
        {
            return new ClaudetteCopy(AppInstallKind.Msix, family, version);
        }
        if (OperatingSystem.IsMacOS() && MacAppInstaller.FindBundle(AppContext.BaseDirectory) is { } bundle)
        {
            return new ClaudetteCopy(AppInstallKind.MacApp, bundle, version);
        }
        return new ClaudetteCopy(AppInstallKind.Other, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), version);
    }

    /// <summary>
    /// Whether Windows started this Claudette through the MSIX's startup task, which gives it no <c>--login</c> the way
    /// the other entries do.
    /// </summary>
    public static bool StartedByPackageTask() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && WindowsStartupActivation.StartedByStartupTask();
}
