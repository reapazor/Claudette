using Claudette.Core.Sessions;

namespace Claudette.IntegrationTests.Support;

/// <summary>A temporary folder, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix = "claudette-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A child process may still hold a file for a moment; the OS cleans temp eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class FakeClaude
{
    /// <summary>The fake-claude executable, copied next to the tests by the project reference.</summary>
    public static string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-claude.exe" : "fake-claude");
}

internal static class SessionEventExtensions
{
    public static async Task<(T Match, List<SessionEvent> Seen)> ReadUntilAsync<T>(this ClaudeSession session, Func<T, bool>? match = null, TimeSpan? timeout = null)
        where T : SessionEvent
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
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
