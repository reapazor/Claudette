using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Settings;

namespace Claudette.Core.Library;

/// <summary>Who holds a session in the library (DESIGN.md §9, "One machine at a time").</summary>
public abstract record LeaseStatus
{
    private LeaseStatus()
    {
    }

    /// <summary>No lease file: nobody has the session open.</summary>
    public sealed record Free : LeaseStatus;

    /// <summary>This Claudette run holds the lease.</summary>
    public sealed record Mine : LeaseStatus;

    /// <summary>Another machine, or another Claudette run on this one, is actively using the session.</summary>
    public sealed record HeldByOther(string Machine, DateTimeOffset UpdatedAt) : LeaseStatus;

    /// <summary>Someone else held it, but hasn't refreshed the lease in <see cref="LeaseManager.StaleAfter"/>.</summary>
    public sealed record Stale(string Machine, DateTimeOffset UpdatedAt) : LeaseStatus;
}

/// <summary>What <c>lease.json</c> holds.</summary>
/// <param name="Owner">A random id for one Claudette run, so two runs on the same machine are told apart.</param>
internal sealed record LeaseInfo(string Machine, DateTimeOffset UpdatedAt, string Owner);

/// <summary>
/// Keeps a session in the library to one machine at a time (DESIGN.md §9, "One machine at a time"). While a session is
/// open, a small <c>lease.json</c> next to it says who has it; it's refreshed every minute, and one that hasn't been
/// refreshed in 10 minutes counts as stale. If another machine takes the session over, the next refresh notices and
/// raises <see cref="LeaseLost"/>, so that tab can become read-only.
/// </summary>
public sealed class LeaseManager : IDisposable
{
    public const string FileName = "lease.json";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private static JsonSerializerOptions Json => JsonFileStore<SessionRecord>.Options;

    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _held = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="machineName">This machine's name as shown in History (Settings → Sessions).</param>
    public LeaseManager(string machineName, TimeProvider time)
    {
        MachineName = machineName;
        _time = time;
        OwnerId = Guid.NewGuid().ToString("N");
        _timer = time.CreateTimer(_ => RefreshAll(), null, RefreshInterval, RefreshInterval);
    }

    /// <summary>A session's lease was taken over by another machine: (session id, that machine's name).</summary>
    public event Action<string, string>? LeaseLost;

    public string MachineName { get; }

    /// <summary>Identifies this Claudette run in lease files.</summary>
    public string OwnerId { get; }

    /// <summary>The sessions whose leases this run holds.</summary>
    public IReadOnlyList<string> HeldSessions
    {
        get
        {
            lock (_lock)
            {
                return [.. _held.Keys];
            }
        }
    }

    /// <summary>Who holds the session in <paramref name="sessionFolder"/> (its folder in the library).</summary>
    public LeaseStatus Check(string sessionFolder)
    {
        var lease = ReadLease(sessionFolder);
        if (lease is null)
        {
            return new LeaseStatus.Free();
        }
        if (lease.Owner == OwnerId)
        {
            return new LeaseStatus.Mine();
        }
        return _time.GetUtcNow() - lease.UpdatedAt >= StaleAfter
            ? new LeaseStatus.Stale(lease.Machine, lease.UpdatedAt)
            : new LeaseStatus.HeldByOther(lease.Machine, lease.UpdatedAt);
    }

    /// <summary>
    /// Takes the lease and keeps it fresh until <see cref="Release"/>. Also used for <b>Take over</b>: it overwrites
    /// whatever lease is there, and the other machine finds out on its next refresh.
    /// </summary>
    public void Acquire(string sessionId, string sessionFolder)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Write(sessionFolder);
            _held[sessionId] = sessionFolder;
        }
    }

    /// <summary>Stops refreshing the lease and deletes the file, but only if it's still ours.</summary>
    public void Release(string sessionId)
    {
        lock (_lock)
        {
            if (_held.Remove(sessionId, out var folder))
            {
                DeleteIfMine(folder);
            }
        }
    }

    /// <summary>Refreshes every held lease. Runs every <see cref="RefreshInterval"/>; public so the app can refresh sooner.</summary>
    public void RefreshAll()
    {
        List<(string SessionId, string Machine)>? lost = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            foreach (var (sessionId, folder) in _held.ToArray())
            {
                try
                {
                    if (ReadLease(folder) is { } lease && lease.Owner != OwnerId)
                    {
                        _held.Remove(sessionId);
                        (lost ??= []).Add((sessionId, lease.Machine));
                        continue;
                    }
                    Write(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The folder may be briefly unavailable; try again next time.
                }
            }
        }
        foreach (var (sessionId, machine) in lost ?? [])
        {
            LeaseLost?.Invoke(sessionId, machine);
        }
    }

    /// <summary>Stops the refresh timer and releases every lease this run holds.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var folder in _held.Values)
            {
                DeleteIfMine(folder);
            }
            _held.Clear();
        }
    }

    /// <summary>The lease in a session folder, or null when there's none or it can't be read.</summary>
    internal static LeaseInfo? ReadLease(string sessionFolder)
    {
        try
        {
            var path = Path.Combine(sessionFolder, FileName);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject lease)
            {
                return null;
            }
            var updatedAt = lease.GetString("updatedAt") is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            return new LeaseInfo(lease.GetString("machine") ?? "", updatedAt, lease.GetString("owner") ?? "");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Write(string sessionFolder) =>
        LibraryFiles.WriteText(
            Path.Combine(sessionFolder, FileName),
            JsonSerializer.Serialize(new LeaseInfo(MachineName, _time.GetUtcNow(), OwnerId), Json));

    private void DeleteIfMine(string sessionFolder)
    {
        try
        {
            if (ReadLease(sessionFolder)?.Owner == OwnerId)
            {
                File.Delete(Path.Combine(sessionFolder, FileName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It goes stale on its own.
        }
    }
}
