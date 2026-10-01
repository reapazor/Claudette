using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Claudette.Core.Accessibility;
using Claudette.Core.Processes;
using Claudette.Platform.Notifications.Mac;

namespace Claudette.Platform.Accessibility;

/// <summary>
/// Whether the OS asks apps to reduce motion (DESIGN.md §3, "Accessibility"): Windows' "Show animations in Windows"
/// (<c>SPI_GETCLIENTAREAANIMATION</c>), macOS's "Reduce motion" (<c>accessibilityDisplayShouldReduceMotion</c>), and
/// GNOME's <c>enable-animations</c> on Linux, read with <c>gsettings</c>. Anything that can't be read is no preference.
/// </summary>
public static class SystemMotion
{
    public static ISystemMotion CreateForCurrentOS(IProcessLauncher launcher, TimeProvider time) =>
        OperatingSystem.IsWindows() ? new WindowsMotion()
        : OperatingSystem.IsMacOS() ? new MacMotion()
        : OperatingSystem.IsLinux() ? new GnomeMotion(launcher, time)
        : new NoSystemMotion();
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsMotion : ISystemMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    public Task<bool> PrefersReducedMotionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var animate, 0) && animate == 0);
}

[SupportedOSPlatform("macos")]
internal sealed class MacMotion : ISystemMotion
{
    public Task<bool> PrefersReducedMotionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var workspace = ObjC.Send(ObjC.GetClass("NSWorkspace"), "sharedWorkspace");
            // A BOOL comes back in the low byte.
            return Task.FromResult(workspace != 0 && (ObjC.Send(workspace, "accessibilityDisplayShouldReduceMotion") & 0xFF) != 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult(false);
        }
    }
}

internal sealed class GnomeMotion(IProcessLauncher launcher, TimeProvider time) : ISystemMotion
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<bool> PrefersReducedMotionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                    launcher,
                    new ProcessStartSpec("gsettings", ["get", "org.gnome.desktop.interface", "enable-animations"]),
                    Timeout,
                    time,
                    cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0 && result.StandardOutput.Trim() == "false";
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // No gsettings, or no GNOME: no preference.
            return false;
        }
    }
}
