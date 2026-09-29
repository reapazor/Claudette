using Avalonia;

namespace Claudette.App;

internal sealed class Program
{
    // Don't use Avalonia, third-party APIs or anything that needs a SynchronizationContext before AppMain is
    // called: they aren't initialized yet.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsIdentity();
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
