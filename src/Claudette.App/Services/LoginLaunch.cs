using System.Diagnostics;
using Claudette.Core;
using Claudette.Core.LoginItems;
using Claudette.Core.Processes;
using Claudette.Core.Settings;
using Claudette.Core.Updates;
using Claudette.Platform.LoginItems;

namespace Claudette.App.Services;

/// <summary>Starting Claudette at login (DESIGN.md §9, "Starting at login"), as far as launching goes.</summary>
public static class LoginLaunch
{
    /// <summary>
    /// The OS's login entry. A Claudette started with <c>CLAUDETTE_HOME</c> gets none: the entry would start Claudette
    /// without it, on the real profile.
    /// </summary>
    public static ILoginItems CreateItems(IProcessLauncher launcher, TimeProvider timeProvider) =>
        Environment.GetEnvironmentVariable("CLAUDETTE_HOME") is { Length: > 0 }
            ? new NoLoginItems("Not while CLAUDETTE_HOME is set: Claudette would start at login without it.")
            : LoginItemPlatforms.CreateForCurrentOS(launcher, timeProvider);

    /// <summary>This copy of Claudette: a source build by its build output, anything else as it's installed.</summary>
    public static ClaudetteCopy CurrentCopy(IReadOnlyList<string> args, AppVersion version) =>
        DevelopmentLaunch.Current(args) is { } build
            ? new ClaudetteCopy(AppInstallKind.SourceBuild, Path.TrimEndingDirectorySeparator(build.SourceOutput), version.ToString())
            : LoginItemPlatforms.CurrentCopy(version.ToString());

    /// <summary>
    /// For a launch with <c>--login</c>: when a better copy of Claudette is installed (the release, for a source build),
    /// starts it instead and returns true, so this one exits. That's how a source build's entry starts the MSIX, whose
    /// own startup task only the MSIX can turn on.
    /// </summary>
    public static bool TryHandOver(IReadOnlyList<string> args, AppPaths paths, IProcessLauncher launcher)
    {
        try
        {
            var items = CreateItems(launcher, TimeProvider.System);
            var state = new JsonFileStore<AppState>(paths.StateFile).Load();
            var startAtLogin = new StartAtLogin(items, CurrentCopy(args, AppServices.BuiltVersion()), state.LoginItem, save: () => { });
            if (startAtLogin.HandOverAtLogin() is not { } better)
            {
                return false;
            }
            items.Start(better);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Trace.WriteLine($"Couldn't hand over to the installed Claudette at login, so starting this one: {ex}");
            return false;
        }
    }
}
