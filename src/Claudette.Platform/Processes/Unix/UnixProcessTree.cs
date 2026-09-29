using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Processes.Unix;

/// <summary>A process as a system-wide scan sees it: enough to walk parent links and tell reused PIDs apart.</summary>
internal interface IScannedProcess
{
    int Pid { get; }

    int ParentPid { get; }

    /// <summary>When the process started, in the platform's units. Identifies the process together with its PID.</summary>
    long StartKey { get; }
}

/// <summary>
/// A process tree on macOS or Linux, found by walking parent links from the root (DESIGN.md §4, Process monitor). A
/// process that has been seen and is then re-parented stays listed, marked detached, until it exits.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal abstract class UnixProcessTree<TProcess> : ProcessTree
    where TProcess : IScannedProcess
{
    private readonly TreeMembership _membership;
    private readonly SampleHistory _history;
    private readonly Lock _lock = new();

    protected UnixProcessTree(int rootPid, TimeProvider time, ILogger logger)
        : base(rootPid)
    {
        Time = time;
        Logger = logger;
        _membership = new TreeMembership(rootPid);
        _history = new SampleHistory(time, cpuDivisor: 1);
    }

    protected TimeProvider Time { get; }

    protected ILogger Logger { get; }

    protected abstract int SigStop { get; }

    protected abstract int SigCont { get; }

    /// <summary>Every live process on the system, zombies excluded. Null if the scan failed.</summary>
    protected abstract Dictionary<int, TProcess>? ScanAll();

    /// <summary>The details of a process in the tree, or null if it can no longer be read.</summary>
    protected abstract RawProcess? Describe(TProcess process, bool includeCommandLines, bool isRoot, bool isDetached);

    /// <summary>Whether the process that had <paramref name="pid"/> when it was found is still running.</summary>
    protected abstract bool IsAlive(int pid, long startKey);

    public override IReadOnlyList<ProcessSnapshot> Sample(bool includeCommandLines)
    {
        lock (_lock)
        {
            if (IsDisposed || ScanAll() is not { } all)
            {
                return [];
            }
            var processes = new List<RawProcess>();
            foreach (var member in Walk(all))
            {
                if (Describe(all[member.Pid], includeCommandLines, member.IsRoot, member.IsDetached) is { } process)
                {
                    processes.Add(process);
                }
            }
            return _history.Update(processes);
        }
    }

    public override IReadOnlyList<int> DescendantIds()
    {
        lock (_lock)
        {
            if (IsDisposed || ScanAll() is not { } all)
            {
                return [];
            }
            return [.. Walk(all).Where(m => !m.IsRoot).Select(m => m.Pid)];
        }
    }

    /// <summary>
    /// Freezes the tree top-down with <c>SIGSTOP</c>, scanning again until nothing new appears (so no process can
    /// start another unseen), then sends <c>SIGKILL</c>, children first.
    /// </summary>
    public override void KillAll()
    {
        lock (_lock)
        {
            if (IsDisposed)
            {
                return;
            }
            var stopped = new List<TreeMember>();
            var seen = new HashSet<(int, long)>();
            for (var round = 0; round < 4; round++)
            {
                if (ScanAll() is not { } all)
                {
                    Logger.LogWarning("Couldn't list processes; ending only the tree of PID {Pid} that is already known.", RootPid);
                    break;
                }
                var fresh = Walk(all).Where(m => seen.Add((m.Pid, m.StartKey))).ToList();
                if (fresh.Count == 0)
                {
                    break;
                }
                foreach (var member in fresh)
                {
                    LibC.Kill(member.Pid, SigStop);
                }
                stopped.AddRange(fresh);
            }
            if (stopped.Count == 0)
            {
                LibC.Kill(RootPid, LibC.SigKill);
                return;
            }
            for (var i = stopped.Count - 1; i >= 0; i--)
            {
                var pid = stopped[i].Pid;
                if (LibC.Kill(pid, LibC.SigKill) != 0 && Marshal.GetLastPInvokeError() == LibC.Eperm)
                {
                    // Not ours to end (a setuid program, say): don't leave it frozen.
                    LibC.Kill(pid, SigCont);
                }
            }
        }
    }

    public override async Task StopAsync(int pid, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        // kill(2) with 0 or a negative PID signals whole process groups.
        if (pid <= 0)
        {
            return;
        }
        long startKey;
        lock (_lock)
        {
            if (IsDisposed || ScanAll() is not { } all)
            {
                return;
            }
            var member = Walk(all).FirstOrDefault(m => m.Pid == pid);
            if (member.Pid != pid)
            {
                return;
            }
            startKey = member.StartKey;
        }

        if (LibC.Kill(pid, LibC.SigTerm) != 0)
        {
            return;
        }
        var exited = await Polling.UntilAsync(() => !IsAlive(pid, startKey), grace, Time, cancellationToken).ConfigureAwait(false);
        if (!exited && IsAlive(pid, startKey))
        {
            LibC.Kill(pid, LibC.SigKill);
        }
    }

    private IReadOnlyList<TreeMember> Walk(Dictionary<int, TProcess> all)
    {
        var parentOf = new Dictionary<int, int>(all.Count);
        foreach (var (pid, process) in all)
        {
            parentOf[pid] = process.ParentPid;
        }
        return _membership.Update(parentOf, pid => all.TryGetValue(pid, out var process) ? process.StartKey : null);
    }
}
