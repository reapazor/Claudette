using System.Runtime.Versioning;
using System.Text;
using Claudette.Core.LoginItems;
using Claudette.Core.Processes;
using Claudette.Core.ProjectTools;
using Claudette.Core.Updates;
using Claudette.Platform.Notifications.Windows;
using Claudette.Platform.Updates.Windows;
using Microsoft.Win32;

namespace Claudette.Platform.LoginItems.Windows;

/// <summary>
/// Starting Claudette at login on Windows (DESIGN.md §9, "Starting at login"): a <c>Claudette</c> value under the Run key,
/// which any copy of Claudette can write, and in the MSIX the package's startup task. Task Manager's Startup apps turns
/// a Run value off with a value of the same name under <c>StartupApproved\Run</c>, whose first byte is odd.
/// </summary>
/// <param name="runKey">The Run key under HKCU. Tests use a key of their own.</param>
/// <param name="approvedKey">Where Task Manager keeps which Run values are turned off.</param>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class WindowsLoginItems(IProcessLauncher launcher, TimeProvider timeProvider, string runKey = WindowsLoginItems.RunKey, string approvedKey = WindowsLoginItems.ApprovedKey)
    : ILoginItems
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const string ValueName = "Claudette";

    public string? UnavailableReason => null;

    public string SystemSettingsName => "Task Manager's Startup apps";

    public IPackageStartupTask? PackageTask { get; } = WindowsAppIdentity.IsPackaged ? new MsixStartupTask(timeProvider) : null;

    public LoginEntryState ReadEntry()
    {
        if (ReadCommand() is null)
        {
            return LoginEntryState.Missing;
        }
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey);
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1
            ? LoginEntryState.DisabledByUser
            : LoginEntryState.Enabled;
    }

    /// <summary>The Run value: the command Windows runs at login, or null when there's none.</summary>
    public string? ReadCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(runKey);
        return run?.GetValue(ValueName) as string;
    }

    public void WriteEntry(ClaudetteCopy copy)
    {
        var command = Command(copy);
        using var run = Registry.CurrentUser.CreateSubKey(runKey);
        run.SetValue(ValueName, Format(command), RegistryValueKind.String);
    }

    /// <summary>Removes the Run value, and Task Manager's note about it, so a later entry starts out turned on.</summary>
    public void DeleteEntry()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true))
        {
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public bool Exists(ClaudetteCopy copy) =>
        copy.Kind == AppInstallKind.Msix ? Deployment.IsFamilyInstalled(copy.Location) : copy.ExistsOnDisk();

    public void Start(ClaudetteCopy copy)
    {
        if (copy.Kind == AppInstallKind.Msix)
        {
            // Started as the Start menu starts it, so it has its package identity, with the argument a Run value can't give it.
            Deployment.CreateActivationManager().ActivateApplication($"{copy.Location}!{MsixInstaller.ApplicationId}", LoginCommand.LoginOption, 0);
            return;
        }
        var command = Command(copy);
        launcher.Start(new ProcessStartSpec(command.Program, command.Arguments) { Detached = true });
    }

    /// <summary>A command as a Run value: the program in quotes, and each argument quoted as Windows reads them back.</summary>
    public static string Format(LoginCommand command) =>
        string.Join(' ', [$"\"{command.Program}\"", .. command.Arguments.Select(Quote)]);

    /// <summary>Quotes an argument so <c>CommandLineToArgvW</c> reads it back as it was.</summary>
    internal static string Quote(string argument) => CommandLines.QuoteForWindows(argument);

    private static LoginCommand Command(ClaudetteCopy copy) =>
        LoginCommand.For(copy, windows: true, LoginCommand.DotnetHost(Environment.ProcessPath))
        ?? throw new InvalidOperationException("Only the MSIX's startup task can start it at login.");
}
