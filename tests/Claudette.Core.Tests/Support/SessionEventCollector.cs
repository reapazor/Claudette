using Claudette.Core.Sessions;

namespace Claudette.Core.Tests.Support;

internal static class SessionEventExtensions
{
    /// <summary>Reads events until one of type <typeparamref name="T"/> (matching <paramref name="match"/>) arrives.</summary>
    public static async Task<(T Match, List<SessionEvent> Seen)> ReadUntilAsync<T>(this ClaudeSession session, Func<T, bool>? match = null, TimeSpan? timeout = null)
        where T : SessionEvent
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var seen = new List<SessionEvent>();
        await foreach (var e in session.Events.ReadAllAsync(cts.Token))
        {
            seen.Add(e);
            if (e is T typed && (match is null || match(typed)))
            {
                return (typed, seen);
            }
        }
        throw new InvalidOperationException($"The event stream ended before a {typeof(T).Name} arrived. Saw: {string.Join(", ", seen.Select(s => s.GetType().Name))}");
    }
}
