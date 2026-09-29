namespace Claudette.Core.LoginItems;

/// <summary>The login entry that runs a command: the Run key's value, the LaunchAgent, the autostart file.</summary>
public enum LoginEntryState
{
    Missing,
    Enabled,

    /// <summary>There, but turned off outside Claudette, such as in Task Manager's Startup apps.</summary>
    DisabledByUser,
}

/// <summary>The MSIX package's startup task, as <c>Windows.ApplicationModel.StartupTaskState</c> has it.</summary>
public enum PackageTaskState
{
    Disabled = 0,

    /// <summary>Turned off in Task Manager or Settings. Only the user can turn it on again.</summary>
    DisabledByUser = 1,

    Enabled = 2,
    DisabledByPolicy = 3,
    EnabledByPolicy = 4,
}

/// <summary>
/// The OS's ways of starting Claudette when the user logs in (DESIGN.md §9, "Starting at login"): a command entry that
/// any copy of Claudette can write, and, in the MSIX, the package's own startup task. Implemented in Claudette.Platform:
/// the Run key and <c>StartupTask</c> on Windows, a LaunchAgent on macOS, an XDG autostart file on Linux.
/// </summary>
public interface ILoginItems
{
    /// <summary>Why Claudette can't start at login here, or null when it can.</summary>
    string? UnavailableReason { get; }

    /// <summary>Where the user turns it on and off outside Claudette, such as "Task Manager's Startup apps".</summary>
    string SystemSettingsName { get; }

    LoginEntryState ReadEntry();

    /// <summary>Makes the command entry start <paramref name="copy"/> with <c>--login</c>, replacing what it started before.</summary>
    void WriteEntry(ClaudetteCopy copy);

    /// <summary>Removes the command entry, if there is one.</summary>
    void DeleteEntry();

    /// <summary>The package's startup task when this Claudette runs from the MSIX, the only one that can change it. Null otherwise.</summary>
    IPackageStartupTask? PackageTask { get; }

    /// <summary>Whether <paramref name="copy"/> is still on this machine.</summary>
    bool Exists(ClaudetteCopy copy);

    /// <summary>Starts <paramref name="copy"/> with <c>--login</c>, to hand over to it at login.</summary>
    void Start(ClaudetteCopy copy);
}

/// <summary>The MSIX package's startup task (<c>windows.startupTask</c> in its manifest).</summary>
public interface IPackageStartupTask
{
    Task<PackageTaskState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns it on, unless the user or a policy turned it off, and returns the state it's in after.</summary>
    Task<PackageTaskState> RequestEnableAsync(CancellationToken cancellationToken = default);

    Task DisableAsync(CancellationToken cancellationToken = default);
}

/// <summary>No way to start Claudette at login: tests, other OSes, and a Claudette started with <c>CLAUDETTE_HOME</c>.</summary>
public sealed class NoLoginItems(string reason) : ILoginItems
{
    public string? UnavailableReason => reason;

    public string SystemSettingsName => "the OS's settings";

    public LoginEntryState ReadEntry() => LoginEntryState.Missing;

    public void WriteEntry(ClaudetteCopy copy) => throw new InvalidOperationException(reason);

    public void DeleteEntry()
    {
    }

    public IPackageStartupTask? PackageTask => null;

    public bool Exists(ClaudetteCopy copy) => copy.ExistsOnDisk();

    public void Start(ClaudetteCopy copy) => throw new InvalidOperationException(reason);
}
