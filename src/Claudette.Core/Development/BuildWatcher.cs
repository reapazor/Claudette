namespace Claudette.Core.Development;

/// <summary>
/// Watches a source build's output for a newer build (DESIGN.md §9, "Working on Claudette"). A build counts once its
/// files have stopped changing from one check to the next, so one that's still being written isn't offered.
/// </summary>
public sealed class BuildWatcher : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime?> _readStamp;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private DateTime _known;
    private DateTime? _settling;

    /// <param name="outputDirectory">The build output to watch.</param>
    /// <param name="assemblyName">The app's assembly, which a complete build has.</param>
    /// <param name="runningStamp">The build running now: only a newer one counts.</param>
    /// <param name="readStamp">For tests: reads the build's stamp instead of the folder.</param>
    public BuildWatcher(string outputDirectory, string assemblyName, DateTime runningStamp, TimeProvider time, Func<DateTime?>? readStamp = null)
    {
        _readStamp = readStamp ?? (() => BuildOutput.IsComplete(outputDirectory, assemblyName) ? BuildOutput.LatestWrite(outputDirectory) : null);
        _known = runningStamp;
        _timer = time.CreateTimer(_ => Poll(), null, PollInterval, PollInterval);
    }

    /// <summary>Raised on a timer thread with the new build's stamp, once per build.</summary>
    public event Action<DateTime>? BuildReady;

    /// <summary>Checks the build output now. Runs every <see cref="PollInterval"/>.</summary>
    public void Poll()
    {
        DateTime ready;
        lock (_lock)
        {
            DateTime? stamp;
            try
            {
                stamp = _readStamp();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mid-build; look again next time.
                return;
            }
            if (stamp is not { } current || current <= _known)
            {
                _settling = null;
                return;
            }
            if (_settling != current)
            {
                _settling = current;
                return;
            }
            _known = current;
            _settling = null;
            ready = current;
        }
        BuildReady?.Invoke(ready);
    }

    public void Dispose() => _timer.Dispose();
}
