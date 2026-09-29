using System.ComponentModel;
using Claudette.Core.Credentials;
using Claudette.Core.Diffs;
using Claudette.Core.Perforce;
using Claudette.Core.Processes;
using Claudette.Core.Settings;

namespace Claudette.App.Services;

/// <summary>
/// What Perforce handling shares between tabs (DESIGN.md §18): the <c>p4</c> runner, one login at a time per server
/// and user, the OS credential store for passwords, and opening changelists in P4V.
/// </summary>
public sealed class PerforceService(AppServices services, ICredentialStore credentials)
{
    /// <summary>
    /// How long Claude Code waits for the PreToolUse hook. When a password prompt is still open near the end, the hook
    /// lets the command run anyway (it fails, and recovery takes over once the user answers), because a hook that times
    /// out stops the command without running it.
    /// </summary>
    public static readonly TimeSpan HookTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The hook answers by itself this long before Claude Code would give up on it.</summary>
    public static readonly TimeSpan HookMargin = TimeSpan.FromSeconds(15);

    public PerforceClient Client { get; } = new(services.Launcher, services.Time);

    public PerforceLoginGate Gate { get; } = new();

    /// <summary>Where "Stored by Claudette" keeps passwords. Tests replace it.</summary>
    public ICredentialStore Credentials { get; internal set; } = credentials;

    public PerforceSettings Settings => services.Settings.Perforce;

    /// <summary>The server and user of each workspace a tab found, for Settings → Perforce's stored password.</summary>
    public IReadOnlyList<(string Server, string User)> KnownLogins
    {
        get
        {
            lock (_knownLogins)
            {
                return _knownLogins.ToArray();
            }
        }
    }

    private readonly List<(string Server, string User)> _knownLogins = [];

    public void AddKnownLogin(string server, string user)
    {
        lock (_knownLogins)
        {
            if (!_knownLogins.Contains((server, user)))
            {
                _knownLogins.Add((server, user));
            }
        }
    }

    /// <summary>Finds P4V for <b>Open in P4V</b>. Tests replace it.</summary>
    internal IFileProbe Probe { get; set; } = FileProbe.Instance;

    /// <summary>The tab's folder, with Settings → Perforce's per-folder server and user, if any.</summary>
    public PerforceTarget TargetFor(string folder) =>
        Settings.OverrideFor(folder) is { } o
            ? new PerforceTarget(folder, string.IsNullOrWhiteSpace(o.Server) ? null : o.Server.Trim(), string.IsNullOrWhiteSpace(o.User) ? null : o.User.Trim())
            : new PerforceTarget(folder);

    public PerforceKeeperOptions KeeperOptions() =>
        new(TimeSpan.FromMinutes(Math.Clamp(Settings.RenewBeforeMinutes, 1, 24 * 60)), Settings.AllHostsTickets);

    /// <summary>What a stored password is labeled as in the OS credential store.</summary>
    public static string CredentialLabel(string server, string user) => $"Claudette: Perforce {user} @ {server}";

    public static string CredentialKey(string server, string user) => $"perforce/{server}/{user}";

    /// <summary>
    /// <b>Open in P4V</b>: <c>p4v -p &lt;server&gt; -u &lt;user&gt; -c &lt;workspace&gt; -cmd "open changelist 12345"</c>.
    /// Throws when P4V can't be started.
    /// </summary>
    public void OpenInP4V(long changelist, string folder, PerforceWorkspace? workspace, PerforceTarget? target)
    {
        var args = new List<string>();
        if ((target?.Port ?? workspace?.Port) is { Length: > 0 } port)
        {
            args.AddRange(["-p", port]);
        }
        if ((target?.User ?? workspace?.User) is { Length: > 0 } user)
        {
            args.AddRange(["-u", user]);
        }
        if (workspace?.Client is { Length: > 0 } client)
        {
            args.AddRange(["-c", client]);
        }
        args.AddRange(["-cmd", $"open changelist {changelist}"]);
        var spec = new ProcessStartSpec(FindP4V(), args) { WorkingDirectory = Directory.Exists(folder) ? folder : null, Detached = true };
        try
        {
            _ = services.Launcher.Start(spec);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("P4V wasn't found. Install P4V, or put p4v on your PATH.", ex);
        }
    }

    /// <summary>P4V on the PATH, else where its installers put it; "p4v" to let the OS look.</summary>
    public string FindP4V()
    {
        if (Probe.FindOnPath(OperatingSystem.IsWindows() ? "p4v.exe" : "p4v") is { } onPath)
        {
            return onPath;
        }
        string[] known = OperatingSystem.IsWindows()
            ? [Probe.ExpandEnvironmentVariables(@"%ProgramFiles%\Perforce\p4v.exe"), Probe.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Perforce\p4v.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/p4v.app/Contents/MacOS/p4v", "/Applications/Perforce/p4v.app/Contents/MacOS/p4v"]
                : ["/usr/local/bin/p4v", "/opt/p4v/bin/p4v"];
        return known.FirstOrDefault(Probe.FileExists) ?? "p4v";
    }
}
