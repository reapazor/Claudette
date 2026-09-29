namespace Claudette.Platform.Processes;

internal static class Polling
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    /// <summary>Checks <paramref name="done"/> every 50 ms until it's true or <paramref name="timeout"/> has passed.</summary>
    /// <returns>Whether <paramref name="done"/> became true.</returns>
    public static async Task<bool> UntilAsync(Func<bool> done, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        var start = time.GetTimestamp();
        while (!done())
        {
            var left = timeout - time.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
            {
                return false;
            }
            await Task.Delay(left < Step ? left : Step, time, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }
}
