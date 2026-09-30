namespace Claudette.Platform.Processes;

/// <summary>
/// A tab's <c>claude</c> process and everything it has started (DESIGN.md §4, Process monitor).
/// <list type="bullet">
/// <item>Windows: a Job Object holds the root and every descendant, even after a parent exits.</item>
/// <item>macOS and Linux: the tree is found by walking parent links from the root. A process that has been seen and
/// is then re-parented (for example a daemonized dev server) stays listed, marked detached, until it exits.</item>
/// </list>
/// Disposing releases the OS handles; it never ends any process.
/// </summary>
public abstract class ProcessTree : IDisposable
{
    private int _disposed;

    protected ProcessTree(int rootPid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootPid);
        RootPid = rootPid;
    }

    public int RootPid { get; }

    /// <summary>
    /// True when a Job Object tracks the tree (Windows). False when the tree is found by walking parent links: always on
    /// macOS and Linux, and on Windows when the root couldn't be put in a job.
    /// </summary>
    public virtual bool IsJobBacked => false;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Called once, after the tree is disposed. Used by the tracker to forget it.</summary>
    internal Action<ProcessTree>? Disposed { get; set; }

    /// <summary>
    /// The root (while it runs) and every live descendant, root first. Processes that can't be read are left out; this
    /// never throws for a process that exits mid-sample. Returns an empty list once disposed.
    /// </summary>
    /// <param name="includeCommandLines">Read command lines too. They can hold secrets, so they're read only when shown.</param>
    public abstract IReadOnlyList<ProcessSnapshot> Sample(bool includeCommandLines);

    /// <summary>The PIDs of the live descendants, not including the root.</summary>
    public abstract IReadOnlyList<int> DescendantIds();

    /// <summary>Ends the root and every descendant immediately. Used when a tab closes, so nothing is left running.</summary>
    public abstract void KillAll();

    /// <summary>
    /// How long <see cref="StopAsync"/> waits for a process it ended to be gone. Ending one only marks it: it goes
    /// when the OS next runs it, which a busy machine can put off.
    /// </summary>
    protected static readonly TimeSpan EndWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Stops one process in the tree: asks it to exit, waits up to <paramref name="grace"/>, then ends it and waits for
    /// it to be gone. Does nothing if <paramref name="pid"/> isn't a live process in this tree.
    /// <list type="bullet">
    /// <item>macOS and Linux: <c>SIGTERM</c>, then <c>SIGKILL</c>.</item>
    /// <item>Windows: asks the process to close its main window, then terminates it. Console programs (most of what
    /// Claude Code starts) have no window and can't be asked to exit gracefully by another process, so they're
    /// terminated straight away.</item>
    /// </list>
    /// </summary>
    public abstract Task StopAsync(int pid, TimeSpan grace, CancellationToken cancellationToken = default);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Dispose(true);
        GC.SuppressFinalize(this);
        Disposed?.Invoke(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
