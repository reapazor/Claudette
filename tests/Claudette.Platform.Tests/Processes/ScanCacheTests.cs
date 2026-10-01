using Claudette.Platform.Processes.Unix;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Platform.Tests.Processes;

/// <summary>One system scan shared by every tab's tree for sampling (DESIGN.md §4, Process monitor).</summary>
public sealed class ScanCacheTests
{
    private sealed record Entry(int Pid, int ParentPid, long StartKey) : IScannedProcess;

    [Fact]
    public void Trees_sampling_within_a_second_share_one_scan()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The macOS and Linux trees scan the system.");
        var time = new FakeTimeProvider();
        var cache = new ScanCache<Entry>(time, TimeSpan.FromSeconds(1));
        var scans = 0;
        IReadOnlyDictionary<int, Entry> Scan()
        {
            scans++;
            return new Dictionary<int, Entry> { [1] = new(1, 0, 10) };
        }

        var first = cache.Get(Scan);
        var second = cache.Get(Scan);
        time.Advance(TimeSpan.FromMilliseconds(999));
        cache.Get(Scan);

        Assert.Equal(1, scans);
        Assert.Same(first, second);

        time.Advance(TimeSpan.FromMilliseconds(1));
        cache.Get(Scan);
        Assert.Equal(2, scans);
    }

    [Fact]
    public void A_failed_scan_isnt_kept()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The macOS and Linux trees scan the system.");
        var cache = new ScanCache<Entry>(new FakeTimeProvider(), TimeSpan.FromSeconds(1));
        var scans = 0;

        Assert.Null(cache.Get(() => { scans++; return null; }));
        Assert.NotNull(cache.Get(() => { scans++; return new Dictionary<int, Entry>(); }));
        Assert.Equal(2, scans);
    }
}
