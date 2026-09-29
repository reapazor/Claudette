using System.Collections.Concurrent;

namespace Claudette.Core.Perforce;

/// <summary>
/// Lets one login at a time run per server and user, across every tab. Tabs in the same workspace share one ticket
/// (in P4TICKETS), so a second tab waits for the first tab's login and then finds the ticket valid, instead of asking
/// for the password again.
/// </summary>
public sealed class PerforceLoginGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
