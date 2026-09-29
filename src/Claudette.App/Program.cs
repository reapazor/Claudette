using Avalonia;
using Claudette.Core;
using Claudette.Platform;

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
        // One Claudette per user and data folder: a second launch hands its arguments over and exits.
        var instance = new SingleInstance(AppPaths.ForCurrentUser().DataDirectory);
        if (instance.TryHandOffAsync(args).GetAwaiter().GetResult())
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
            Platform.Notifications.Windows.WindowsAppIdentity.Initialize(iconPath: null);
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
