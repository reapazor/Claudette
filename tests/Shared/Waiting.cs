using System.Diagnostics;

namespace Claudette.Tests;

/// <summary>
/// Waiting in tests for real work: processes, background threads and rendering. By the clock, never by counting loops,
/// which a busy CI machine runs through before the work is done. Simulated time is <c>FakeTimeProvider</c>'s job.
/// </summary>
internal static class Waiting
{
    /// <summary>Long enough for a busy CI machine to start a process.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    /// <summary>Waits until <paramref name="condition"/> holds, failing the test after <see cref="Timeout"/>.</summary>
    /// <param name="poll">Run before each check, such as running a window's queued UI work.</param>
    public static async Task UntilAsync(Func<bool> condition, string? what = null, TimeSpan? timeout = null, Action? poll = null)
    {
        var limit = timeout ?? Timeout;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            poll?.Invoke();
            if (condition())
            {
                return;
            }
            if (clock.Elapsed >= limit)
            {
                Assert.Fail($"Timed out after {limit.TotalSeconds:0.#} s waiting for {what ?? "the condition"}.");
            }
            await Task.Delay(Interval, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Checks that <paramref name="condition"/> stays false for <paramref name="period"/>, failing as soon as it doesn't:
    /// for "this didn't happen", where nothing marks the moment it would have.
    /// </summary>
    public static async Task NeverAsync(Func<bool> condition, string what, TimeSpan? period = null)
    {
        var limit = period ?? TimeSpan.FromMilliseconds(200);
        var clock = Stopwatch.StartNew();
        do
        {
            Assert.False(condition(), $"Unexpectedly: {what}.");
            await Task.Delay(Interval, TestContext.Current.CancellationToken);
        }
        while (clock.Elapsed < limit);
        Assert.False(condition(), $"Unexpectedly: {what}.");
    }
}
