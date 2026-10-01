namespace Claudette.Platform.Processes.Unix;

/// <summary>
/// One scan of every process on the system, shared by all the tabs' trees for a moment (DESIGN.md §4, Process monitor).
/// Each tab samples on its own timer, and each sample scanned all of <c>/proc</c>, or ran <c>ps</c> on macOS: with
/// several tabs, as many scans as tabs every tick. Tabs in the background, which only show a summary every 10
/// seconds, use a scan this recent instead. The Processes page, stopping a process and ending a tree always scan
/// afresh, and a fresh scan is shared too.
/// </summary>
internal sealed class ScanCache<TProcess>(TimeProvider time, TimeSpan freshFor)
    where TProcess : IScannedProcess
{
    /// <summary>Half the summary's sampling interval, so a summary is at most that much behind.</summary>
    public static readonly TimeSpan DefaultFreshFor = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private IReadOnlyDictionary<int, TProcess>? _last;
    private DateTimeOffset _at;
    private long _timestamp;

    /// <summary>
    /// The latest scan, if it's recent enough; otherwise a new one by <paramref name="scan"/>. Trees asking at the same
    /// time wait for one scan rather than each running their own. Null when the scan failed.
    /// </summary>
    /// <param name="fresh">Scan now whatever the last scan's age, and keep the result for the others.</param>
    /// <param name="scannedAt">When the scan returned was made (<see cref="TimeProvider.GetTimestamp"/>), for CPU use.</param>
    public IReadOnlyDictionary<int, TProcess>? Get(Func<IReadOnlyDictionary<int, TProcess>?> scan, bool fresh, out long scannedAt)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (!fresh && _last is not null && now - _at < freshFor && now >= _at)
            {
                scannedAt = _timestamp;
                return _last;
            }
            scannedAt = time.GetTimestamp();
            var result = scan();
            if (result is not null)
            {
                _last = result;
                _at = now;
                _timestamp = scannedAt;
            }
            return result;
        }
    }

    /// <inheritdoc cref="Get(Func{IReadOnlyDictionary{int, TProcess}?}, bool, out long)"/>
    public IReadOnlyDictionary<int, TProcess>? Get(Func<IReadOnlyDictionary<int, TProcess>?> scan, bool fresh = false) => Get(scan, fresh, out _);

    /// <summary>A process was signalled: the last scan may list it, so the next sample scans again.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _last = null;
        }
    }
}
