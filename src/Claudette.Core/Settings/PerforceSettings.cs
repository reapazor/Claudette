using Claudette.Core.Perforce;

namespace Claudette.Core.Settings;

/// <summary>
/// Settings → Perforce (DESIGN.md §18). Ticket handling is off by default. The password itself is never here: it's in
/// the OS credential store, in Perforce's own configuration, or typed each time. Perforce settings stay on each machine
/// (they aren't part of settings sync), because servers, workspaces and stored passwords belong to the machine.
/// </summary>
public sealed class PerforceSettings
{
    public const int DefaultRenewBeforeMinutes = 30;

    /// <summary>Keep Perforce tabs logged in: detect workspaces, add the workspace note, and renew tickets.</summary>
    public bool Enabled { get; set; }

    public PerforcePasswordSource PasswordSource { get; set; } = PerforcePasswordSource.Stored;

    /// <summary>Log in again when less than this many minutes are left on the ticket.</summary>
    public int RenewBeforeMinutes { get; set; } = DefaultRenewBeforeMinutes;

    /// <summary>Request tickets valid on every host (<c>p4 login -a</c>).</summary>
    public bool AllHostsTickets { get; set; }

    /// <summary>A <c>CL 12345</c> badge after the tab's name. The info card always shows the changelist.</summary>
    public bool ShowChangelistOnTabs { get; set; }

    /// <summary>A server or user to use instead of what Perforce resolves, for tabs in a folder.</summary>
    public List<PerforceFolderOverride> FolderOverrides { get; set; } = [];

    /// <summary>The override for <paramref name="folder"/>: the one for the deepest folder containing it.</summary>
    public PerforceFolderOverride? OverrideFor(string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Normalize(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }
        var target = Normalize(folder);
        return FolderOverrides
            .Where(o => o.Folder.Length > 0)
            .Select(o => (Override: o, Folder: Normalize(o.Folder)))
            .Where(o => target.Equals(o.Folder, comparison) || target.StartsWith(Path.EndsInDirectorySeparator(o.Folder) ? o.Folder : o.Folder + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(o => o.Folder.Length)
            .Select(o => o.Override)
            .FirstOrDefault();
    }
}

/// <summary>Per-folder Perforce server and user (Settings → Perforce).</summary>
public sealed class PerforceFolderOverride
{
    public string Folder { get; set; } = "";

    /// <summary>A P4PORT, such as <c>ssl:perforce:1666</c>; null keeps what Perforce resolves.</summary>
    public string? Server { get; set; }

    public string? User { get; set; }
}
