using System.ComponentModel;
using System.Globalization;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;
using Claudette.Core.RemoteControl;

namespace Claudette.Platform.Power;

/// <summary>
/// Holds off system sleep with a helper that blocks it for as long as it runs, and ends the helper to let the computer
/// sleep again (DESIGN.md §18, "Remote Control"): <c>caffeinate</c> on macOS, <c>systemd-inhibit</c> on Linux. The
/// helper starts through <see cref="IProcessLauncher"/> with Claudette's own environment, like <c>ps</c> and
/// <c>notify-send</c>. If it stops by itself, why is kept for Diagnostics.
/// </summary>
public sealed class ProcessSleepBlocker : ISleepBlocker
{
    public const string SystemdInhibitMissing = "systemd-inhibit wasn't found, so Claudette can't keep this computer awake.";

    private readonly IProcessLauncher _launcher;
    private readonly ProcessStartSpec _spec;
    private readonly string _name;
    private readonly Lock _lock = new();
    private IRunningProcess? _process;
    private string? _lastError;

    public ProcessSleepBlocker(IProcessLauncher launcher, ProcessStartSpec spec, string name)
    {
        _launcher = launcher;
        _spec = spec;
        _name = name;
    }

    /// <summary>
    /// <c>caffeinate -i -w &lt;pid&gt;</c>: no idle system sleep until it's ended, or until Claudette itself exits, so a
    /// Claudette that crashed doesn't keep the Mac awake.
    /// </summary>
    public static ProcessSleepBlocker Caffeinate(IProcessLauncher launcher, int claudetteProcessId, string path = "/usr/bin/caffeinate") =>
        new(launcher, new ProcessStartSpec(path, ["-i", "-w", claudetteProcessId.ToString(CultureInfo.InvariantCulture)]), "caffeinate");

    /// <summary>
    /// <c>systemd-inhibit --what=sleep …</c>, which holds a logind inhibitor lock while its command runs: a shell loop that
    /// ends when Claudette does, as <c>caffeinate -w</c> does on macOS, so a Claudette that crashed or was killed doesn't
    /// keep the computer awake. None without <c>systemd-inhibit</c> on the <c>PATH</c>.
    /// </summary>
    public static ISleepBlocker SystemdInhibit(IProcessLauncher launcher, int claudetteProcessId, IFileProbe? probe = null) =>
        (probe ?? FileProbe.Instance).FindOnPath("systemd-inhibit") is { } path
            ? new ProcessSleepBlocker(launcher, new ProcessStartSpec(path, SystemdInhibitArguments(claudetteProcessId)), "systemd-inhibit")
            : new NoSleepBlocker(SystemdInhibitMissing);

    /// <summary>
    /// The inhibitor's command waits while Claudette's process is there (<c>kill -0</c> checks without signalling),
    /// checking every 10 seconds. POSIX <c>sh</c>, rather than GNU <c>tail --pid</c>, which not every distribution has.
    /// </summary>
    public static IReadOnlyList<string> SystemdInhibitArguments(int claudetteProcessId) =>
    [
        "--what=sleep", "--who=Claudette", "--why=Tabs are connected to the Claude app", "--mode=block",
        "/bin/sh", "-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 10; done", "claudette-awake",
        claudetteProcessId.ToString(CultureInfo.InvariantCulture),
    ];

    public string? UnavailableReason => null;

    public bool IsBlocking
    {
        get
        {
            lock (_lock)
            {
                return _process is not null;
            }
        }
    }

    public void SetBlocking(bool block)
    {
        IRunningProcess? stop = null;
        lock (_lock)
        {
            if (block && _process is null)
            {
                try
                {
                    _process = _launcher.Start(_spec);
                    _lastError = null;
                    _ = WatchAsync(_process);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
                {
                    _lastError = $"{_name} couldn't start: {ex.Message}";
                }
            }
            else if (!block && _process is not null)
            {
                stop = _process;
                _process = null;
            }
        }
        // Ending it is what lets the computer sleep again. Watching it disposes of it once it has exited.
        stop?.Kill();
    }

    public string Describe()
    {
        lock (_lock)
        {
            return _process is not null ? $"Keeping the computer awake with {_name}. The display can still sleep."
                : _lastError is { } error ? $"Not keeping the computer awake: {error}"
                : "Not keeping the computer awake now.";
        }
    }

    public void Dispose() => SetBlocking(false);

    /// <summary>A helper that stops while it's wanted (logind refused, say) no longer blocks anything: say why.</summary>
    private async Task WatchAsync(IRunningProcess process)
    {
        var lastError = "";
        var errors = Task.Run(async () =>
        {
            await foreach (var line in process.StandardError.ReadAllAsync().ConfigureAwait(false))
            {
                if (line.Trim().Length > 0)
                {
                    lastError = line.Trim();
                }
            }
        });
        int exitCode;
        try
        {
            exitCode = await process.Exited.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            exitCode = -1;
        }
        lock (_lock)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _lastError = $"{_name} stopped (exit code {exitCode}){(lastError.Length > 0 ? $": {lastError}" : ".")}";
            }
        }
        await process.DisposeAsync().ConfigureAwait(false);
    }
}
