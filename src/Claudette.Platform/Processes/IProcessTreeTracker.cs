using System.Runtime.Versioning;
using Claudette.Core.Processes;
using Claudette.Platform.Processes.Unix;
using Claudette.Platform.Processes.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Processes;

/// <summary>Keeps a <see cref="ProcessTree"/> for each tracked root process (DESIGN.md §4, Process monitor).</summary>
public interface IProcessTreeTracker
{
    /// <summary>
    /// Starts tracking <paramref name="rootPid"/> and everything it starts from now on. Call it as soon as the process
    /// starts. Calling it again for the same PID returns the same tree until that tree is disposed.
    /// </summary>
    ProcessTree Track(int rootPid);

    /// <summary>The tree for <paramref name="rootPid"/>, or null if it isn't tracked (or its tree was disposed).</summary>
    ProcessTree? Find(int rootPid);
}

public static class ProcessTreeTracker
{
    /// <summary>The tracker for the OS Claudette is running on.</summary>
    /// <param name="launcher">Runs <c>ps</c> on macOS.</param>
    /// <exception cref="PlatformNotSupportedException">Not Windows, macOS or Linux.</exception>
    public static IProcessTreeTracker CreateForCurrentOS(IProcessLauncher launcher, TimeProvider time, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(time);
        var loggers = loggerFactory ?? NullLoggerFactory.Instance;
        if (OperatingSystem.IsWindows())
        {
            return Windows(time, loggers.CreateLogger<WindowsProcessTree>());
        }
        if (OperatingSystem.IsLinux())
        {
            return Linux(time, loggers.CreateLogger<LinuxProcessTree>());
        }
        if (OperatingSystem.IsMacOS())
        {
            return MacOS(launcher, time, loggers.CreateLogger<MacProcessTree>());
        }
        throw new PlatformNotSupportedException("The process monitor supports Windows, macOS and Linux.");
    }

    [SupportedOSPlatform("windows")]
    private static ProcessTreeRegistry Windows(TimeProvider time, ILogger logger) =>
        new(pid => WindowsProcessTree.Create(pid, time, logger));

    /// <summary>The tabs' trees share their scans for sampling (<see cref="ScanCache{TProcess}"/>).</summary>
    [SupportedOSPlatform("linux")]
    private static ProcessTreeRegistry Linux(TimeProvider time, ILogger logger)
    {
        var scans = new ScanCache<LinuxStat>(time, ScanCache<LinuxStat>.DefaultFreshFor);
        return new(pid => new LinuxProcessTree(pid, time, logger, scans));
    }

    [SupportedOSPlatform("macos")]
    private static ProcessTreeRegistry MacOS(IProcessLauncher launcher, TimeProvider time, ILogger logger)
    {
        var scans = new ScanCache<PsEntry>(time, ScanCache<PsEntry>.DefaultFreshFor);
        return new(pid => new MacProcessTree(pid, launcher, time, logger, scans));
    }
}

/// <summary>The trees being tracked, one per root PID. A tree is forgotten when it's disposed.</summary>
internal sealed class ProcessTreeRegistry(Func<int, ProcessTree> create) : IProcessTreeTracker
{
    private readonly Dictionary<int, ProcessTree> _trees = [];
    private readonly Lock _lock = new();

    public ProcessTree Track(int rootPid)
    {
        lock (_lock)
        {
            if (_trees.TryGetValue(rootPid, out var existing) && !existing.IsDisposed)
            {
                return existing;
            }
            var tree = create(rootPid);
            tree.Disposed = Forget;
            _trees[rootPid] = tree;
            return tree;
        }
    }

    public ProcessTree? Find(int rootPid)
    {
        lock (_lock)
        {
            return _trees.TryGetValue(rootPid, out var tree) && !tree.IsDisposed ? tree : null;
        }
    }

    private void Forget(ProcessTree tree)
    {
        lock (_lock)
        {
            if (_trees.TryGetValue(tree.RootPid, out var current) && ReferenceEquals(current, tree))
            {
                _trees.Remove(tree.RootPid);
            }
        }
    }
}
