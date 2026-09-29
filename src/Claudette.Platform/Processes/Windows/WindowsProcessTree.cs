using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using static Claudette.Platform.Processes.Windows.WindowsNative;

namespace Claudette.Platform.Processes.Windows;

/// <summary>
/// A process tree on Windows (DESIGN.md §4, Process monitor).
/// <list type="bullet">
/// <item>The root goes into its own Job Object, and everything it starts joins the job, so every descendant is tracked
/// even after its parent exits. The job doesn't have <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: the user can choose to
/// leave processes running when a tab closes.</item>
/// <item>Jobs nest on Windows 8 and later, so this works when Claudette itself runs inside a job (VS Code and many
/// terminals do that). If the root still can't be assigned, the tree falls back to walking parent PIDs from a Toolhelp
/// snapshot and <see cref="IsJobBacked"/> is false.</item>
/// <item>Each process is read with the process APIs; command lines come from <c>NtQueryInformationProcess</c>. A process
/// that can't be opened (access denied, already exited) is skipped.</item>
/// <item>The console host Windows starts for console programs (<c>conhost.exe</c>, one for <c>claude</c> itself) isn't
/// something the tab started, so it's left out of samples and <see cref="DescendantIds"/>. <see cref="KillAll"/> still
/// ends it with the rest of the job.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessTree : ProcessTree
{
    private const uint ReadAccess = ProcessQueryLimitedInformation | ProcessVmRead;
    private const uint AssignAccess = ProcessSetQuota | ProcessTerminate | ProcessQueryLimitedInformation;
    private const uint StopAccess = ProcessTerminate | ProcessQueryLimitedInformation;

    private static readonly string ConsoleHostPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "conhost.exe");

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SafeProcessHandle _root;
    private readonly SafeKernelHandle? _job;
    private readonly TreeMembership? _membership;
    private readonly SampleHistory _history;
    private readonly Lock _lock = new();

    private WindowsProcessTree(int rootPid, TimeProvider time, ILogger logger, SafeProcessHandle root, SafeKernelHandle? job, long rootStartKey)
        : base(rootPid)
    {
        _time = time;
        _logger = logger;
        _root = root;
        _job = job;
        _membership = job is null ? new TreeMembership(rootPid, rootStartKey) : null;
        _history = new SampleHistory(time, Environment.ProcessorCount);
    }

    public override bool IsJobBacked => _job is not null;

    /// <summary>Starts tracking <paramref name="rootPid"/>.</summary>
    /// <param name="useJob">False skips the Job Object and uses the fallback, for tests.</param>
    /// <exception cref="InvalidOperationException">The root can't be opened.</exception>
    internal static WindowsProcessTree Create(int rootPid, TimeProvider time, ILogger logger, bool useJob = true)
    {
        // Holding a handle to the root also keeps its PID from being reused while the tree exists.
        var root = OpenProcess(AssignAccess, false, rootPid);
        if (root.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            root.Dispose();
            throw new InvalidOperationException($"Couldn't open process {rootPid} (error {error}).");
        }
        var rootStartKey = GetProcessTimes(root, out var creation, out _, out _, out _) ? creation : 0;

        SafeKernelHandle? job = null;
        if (useJob)
        {
            job = CreateJobObject(0, null);
            if (job.IsInvalid)
            {
                logger.LogWarning("Couldn't create a Job Object (error {Error}); tracking PID {Pid} by parent PIDs instead.", Marshal.GetLastPInvokeError(), rootPid);
                job.Dispose();
                job = null;
            }
            else if (!AssignProcessToJobObject(job, root))
            {
                logger.LogWarning(
                    "Couldn't put PID {Pid} in a Job Object (error {Error}); tracking it by parent PIDs instead.",
                    rootPid,
                    Marshal.GetLastPInvokeError());
                job.Dispose();
                job = null;
            }
        }

        var tree = new WindowsProcessTree(rootPid, time, logger, root, job, rootStartKey);
        if (job is not null)
        {
            tree.AdoptEarlyChildren(rootStartKey);
        }
        return tree;
    }

    public override IReadOnlyList<ProcessSnapshot> Sample(bool includeCommandLines)
    {
        lock (_lock)
        {
            if (IsDisposed)
            {
                return [];
            }
            var raw = _job is not null ? SampleJob(includeCommandLines) : SampleByParents(includeCommandLines);
            return _history.Update(raw);
        }
    }

    public override IReadOnlyList<int> DescendantIds()
    {
        lock (_lock)
        {
            if (IsDisposed)
            {
                return [];
            }
            var descendants = _job is not null
                ? JobProcessIds().Where(pid => pid != RootPid)
                : WalkByParents().Where(m => !m.IsRoot).Select(m => m.Pid);
            return [.. descendants.Where(pid => !IsConsoleHost(pid))];
        }
    }

    public override void KillAll()
    {
        lock (_lock)
        {
            if (IsDisposed)
            {
                return;
            }
            if (_job is not null)
            {
                if (!TerminateJobObject(_job, 1))
                {
                    _logger.LogWarning("Couldn't end the processes of PID {Pid} (error {Error}).", RootPid, Marshal.GetLastPInvokeError());
                }
                return;
            }
            // Parents first: on Windows children keep their parent's PID after it exits, so they can still be found,
            // and a parent that has ended can't start new ones.
            for (var round = 0; round < 3; round++)
            {
                var members = WalkByParents();
                if (members.Count == 0)
                {
                    return;
                }
                foreach (var member in members)
                {
                    using var handle = OpenProcess(StopAccess, false, member.Pid);
                    if (!handle.IsInvalid && StartKeyOf(handle) == member.StartKey)
                    {
                        TerminateProcess(handle, 1);
                    }
                }
            }
        }
    }

    public override async Task StopAsync(int pid, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        using var handle = OpenProcess(StopAccess, false, pid);
        if (handle.IsInvalid || !IsLive(handle) || !IsInTree(pid, handle))
        {
            return;
        }

        if (TryCloseMainWindow(pid))
        {
            await Polling.UntilAsync(() => !IsLive(handle), grace, _time, cancellationToken).ConfigureAwait(false);
        }
        if (IsLive(handle))
        {
            TerminateProcess(handle, 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _job?.Dispose();
                _root.Dispose();
            }
        }
    }

    private bool IsInTree(int pid, SafeProcessHandle handle)
    {
        lock (_lock)
        {
            if (IsDisposed)
            {
                return false;
            }
            if (_job is not null)
            {
                return IsProcessInJob(handle, _job, out var inJob) && inJob;
            }
            var key = StartKeyOf(handle);
            return WalkByParents().Any(m => m.Pid == pid && m.StartKey == key);
        }
    }

    /// <summary>
    /// <c>claude</c> was running for a moment before it joined the job. Anything it started in that moment isn't in the
    /// job, so it's added now.
    /// </summary>
    private void AdoptEarlyChildren(long rootStartKey)
    {
        var membership = new TreeMembership(RootPid, rootStartKey);
        foreach (var member in Walk(membership))
        {
            if (member.IsRoot)
            {
                continue;
            }
            using var handle = OpenProcess(AssignAccess, false, member.Pid);
            if (!handle.IsInvalid && StartKeyOf(handle) == member.StartKey && !AssignProcessToJobObject(_job!, handle))
            {
                _logger.LogDebug("Couldn't add PID {Pid} to the job of PID {Root}.", member.Pid, RootPid);
            }
        }
    }

    private List<RawProcess> SampleJob(bool includeCommandLines)
    {
        var processes = new List<RawProcess>();
        foreach (var pid in JobProcessIds())
        {
            if (TryRead(pid, includeCommandLines, out var process) && !IsConsoleHost(process.ExecutablePath))
            {
                processes.Add(process);
            }
        }
        var attached = TreeMembership.AttachedToRoot([.. processes.Select(p => (p.Pid, p.ParentPid, p.StartKey))], RootPid);
        processes = [.. processes.Select(p => p with { IsRoot = p.Pid == RootPid, IsDetached = !attached.Contains(p.Pid) })];
        // Root first, then parents before their children.
        processes.Sort((a, b) => a.IsRoot != b.IsRoot ? (a.IsRoot ? -1 : 1) : a.StartKey.CompareTo(b.StartKey));
        return processes;
    }

    private List<RawProcess> SampleByParents(bool includeCommandLines)
    {
        var processes = new List<RawProcess>();
        foreach (var member in WalkByParents())
        {
            if (TryRead(member.Pid, includeCommandLines, out var process)
                && process.StartKey == member.StartKey
                && !IsConsoleHost(process.ExecutablePath))
            {
                processes.Add(process with { IsRoot = member.IsRoot, IsDetached = member.IsDetached });
            }
        }
        return processes;
    }

    private IReadOnlyList<TreeMember> WalkByParents() => Walk(_membership!);

    private static IReadOnlyList<TreeMember> Walk(TreeMembership membership)
    {
        var parentOf = SnapshotParents();
        var keys = new Dictionary<int, long?>();
        return membership.Update(parentOf, pid =>
        {
            if (!keys.TryGetValue(pid, out var key))
            {
                using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                keys[pid] = key = handle.IsInvalid || !IsLive(handle) ? null : StartKeyOf(handle);
            }
            return key;
        });
    }

    /// <summary>Every process on the system and its parent PID, from a Toolhelp snapshot.</summary>
    private static unsafe Dictionary<int, int> SnapshotParents()
    {
        var parents = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            return parents;
        }
        var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
        for (var more = Process32First(snapshot, &entry); more; more = Process32Next(snapshot, &entry))
        {
            parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        return parents;
    }

    /// <summary>The PIDs in the job, growing the buffer until they all fit.</summary>
    private unsafe List<int> JobProcessIds()
    {
        const int header = 8; // NumberOfAssignedProcesses and NumberOfProcessIdsInList, then the ULONG_PTR list.
        var capacity = 64;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var size = header + (capacity * IntPtr.Size);
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                var ok = QueryInformationJobObject(_job!, JobObjectBasicProcessIdList, p, (uint)size, out _);
                var assigned = *(uint*)p;
                var listed = *(uint*)(p + 4);
                if (!ok && Marshal.GetLastPInvokeError() != ErrorMoreData)
                {
                    _logger.LogDebug("Couldn't list the job of PID {Pid} (error {Error}).", RootPid, Marshal.GetLastPInvokeError());
                    return [];
                }
                if (ok && listed >= assigned)
                {
                    var pids = new List<int>((int)listed);
                    var list = (nuint*)(p + header);
                    for (var i = 0; i < listed; i++)
                    {
                        pids.Add((int)list[i]);
                    }
                    return pids;
                }
                capacity = Math.Max(capacity * 2, (int)assigned + 16);
            }
        }
        return [];
    }

    /// <summary>Reads one process. False if it can't be opened or has exited.</summary>
    private static bool TryRead(int pid, bool includeCommandLines, [NotNullWhen(true)] out RawProcess? process)
    {
        process = null;
        var handle = OpenProcess(ReadAccess, false, pid);
        if (handle.IsInvalid)
        {
            // Some processes refuse PROCESS_VM_READ; everything but the memory figure works without it.
            handle.Dispose();
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        }
        using (handle)
        {
            if (handle.IsInvalid
                || !IsLive(handle)
                || !GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
            {
                return false;
            }
            var path = ImagePath(handle);
            process = new RawProcess(
                pid,
                ParentPid(handle),
                creation,
                path is null ? $"PID {pid}" : Path.GetFileName(path),
                path,
                includeCommandLines ? CommandLine(handle) : null,
                TimeSpan.FromTicks(kernel + user),
                WorkingSet(handle),
                creation > 0 ? DateTimeOffset.FromFileTime(creation) : null,
                IsRoot: false,
                IsDetached: false);
            return true;
        }
    }

    private static bool IsLive(SafeProcessHandle handle) => GetExitCodeProcess(handle, out var code) && code == StillActive;

    private static bool IsConsoleHost(string? path) => string.Equals(path, ConsoleHostPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsConsoleHost(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        return !handle.IsInvalid && IsConsoleHost(ImagePath(handle));
    }

    private static long? StartKeyOf(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : null;

    private static unsafe int ParentPid(SafeProcessHandle handle)
    {
        ProcessBasicInformation info;
        var status = NtQueryInformationProcess(handle, ProcessBasicInformationClass, &info, (uint)sizeof(ProcessBasicInformation), out _);
        return status >= 0 ? (int)info.InheritedFromUniqueProcessId : 0;
    }

    private static unsafe long WorkingSet(SafeProcessHandle handle)
    {
        var counters = new ProcessMemoryCounters { Cb = (uint)sizeof(ProcessMemoryCounters) };
        return GetProcessMemoryInfo(handle, &counters, counters.Cb) ? (long)counters.WorkingSetSize : 0;
    }

    private static unsafe string? ImagePath(SafeProcessHandle handle)
    {
        foreach (var capacity in (ReadOnlySpan<int>)[260, 32_768])
        {
            var buffer = new char[capacity];
            fixed (char* p = buffer)
            {
                var size = (uint)capacity;
                if (QueryFullProcessImageName(handle, 0, p, ref size))
                {
                    return new string(p, 0, (int)size);
                }
            }
        }
        return null;
    }

    /// <summary>The command line, from <c>ProcessCommandLineInformation</c> (Windows 8.1 and later).</summary>
    private static unsafe string? CommandLine(SafeProcessHandle handle)
    {
        var size = 1024u;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformationClass, p, size, out var needed);
                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall && needed > size)
                {
                    size = needed;
                    continue;
                }
                if (status < 0)
                {
                    return null;
                }
                var text = (UnicodeString*)p;
                return text->Buffer is null ? null : new string(text->Buffer, 0, text->Length / sizeof(char));
            }
        }
        return null;
    }

    private static bool TryCloseMainWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
