using Avalonia;
using Claudette.App.Services;
using Claudette.Core;
using Claudette.Core.Processes;
using Claudette.Platform;
using Claudette.Platform.LoginItems;

namespace Claudette.App;

internal sealed class Program
{
    // Don't use Avalonia, third-party APIs or anything that needs a SynchronizationContext before AppMain is
    // called: they aren't initialized yet.
    /// <summary>Takes the arguments of later launches, such as the jump list's <c>--folder</c> (DESIGN.md §4).</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsIdentity();
        }
        var paths = AppPaths.ForCurrentUser();
        // The MSIX's startup task starts it without arguments: mark it as started at login, as the other entries do.
        if (LoginItemPlatforms.StartedByPackageTask())
        {
            args = [.. args, LaunchArguments.LoginOption];
        }
        // One Claudette per user and data folder: a second launch hands its arguments over and exits. A build that
        // Claudette restarted into takes over instead: the one that started it has stopped listening.
        var instance = new SingleInstance(paths.DataDirectory);
        if (LaunchArguments.RestoreNonce(args) is null && instance.TryHandOffAsync(args).GetAwaiter().GetResult())
        {
            instance.Dispose();
            return;
        }
        // At login, a source build starts the installed release instead (DESIGN.md §9, "Starting at login").
        if (LaunchArguments.IsLogin(args) && LoginLaunch.TryHandOver(args, paths, new ProcessLauncher()))
        {
            instance.Dispose();
            return;
        }
        // A source build runs from a copy of its build output, so it can be rebuilt while it runs (DESIGN.md §9).
        if (DevelopmentLaunch.TryRunFromCopy(args, paths, new ProcessLauncher()))
        {
            instance.Dispose();
            return;
        }
        instance.Listen();
        Instance = instance;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance.Dispose();
        }
    }

    /// <summary>
    /// Toasts, taskbar grouping and the jump list need Claudette's AppUserModelID before the first window exists
    /// (DESIGN.md §10). Nothing here is worth failing to start over.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindowsIdentity()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "claudette.ico");
            Platform.Notifications.Windows.WindowsAppIdentity.Initialize(File.Exists(icon) ? icon : null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Couldn't set Claudette's Windows identity: {ex}");
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
