using Claudette.Core.Processes;
using Claudette.Core.RemoteControl;
using Claudette.Platform.Power.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Power;

public static class SleepBlockers
{
    /// <summary>
    /// What keeps this computer awake while tabs are connected to the Claude app (DESIGN.md §18, "Remote Control"):
    /// <c>SetThreadExecutionState</c> on Windows, <c>caffeinate</c> on macOS, and <c>systemd-inhibit</c> on Linux when
    /// it's installed. Anything that fails to set up keeps nothing awake, and Diagnostics says why.
    /// </summary>
    public static ISleepBlocker CreateForCurrentOS(IProcessLauncher launcher, ILoggerFactory? loggerFactory = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsSleepBlocker();
            }
            if (OperatingSystem.IsMacOS())
            {
                return ProcessSleepBlocker.Caffeinate(launcher, Environment.ProcessId);
            }
            if (OperatingSystem.IsLinux())
            {
                return ProcessSleepBlocker.SystemdInhibit(launcher);
            }
        }
        catch (Exception ex)
        {
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(typeof(SleepBlockers)).LogWarning(ex, "Keeping the computer awake isn't available.");
            return new NoSleepBlocker($"setting it up failed: {ex.Message}");
        }
        return new NoSleepBlocker("Claudette can't keep this kind of computer awake.");
    }
}
