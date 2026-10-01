using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;
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

    /// <summary>
    /// There's a lease file that can't be read right now: a sync client may be writing it, or it's damaged. Nobody
    /// should take the lease or write the session on the strength of that.
    /// </summary>
    public sealed record Unreadable : LeaseStatus;
}

/// <summary>What <c>lease.json</c> holds.</summary>
/// <param name="Owner">A random id for one Claudette run, so two runs on the same machine are told apart.</param>
internal sealed record LeaseInfo(string Machine, DateTimeOffset UpdatedAt, string Owner);

/// <summary>What reading <c>lease.json</c> found: a lease, no file, or a file that couldn't be read.</summary>
internal readonly record struct LeaseRead(LeaseInfo? Lease, bool Unreadable)
{
    public static LeaseRead Missing => default;

    public static LeaseRead Failed => new(null, true);
}

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
        var read = Read(sessionFolder);
        if (read.Unreadable)
        {
            return new LeaseStatus.Unreadable();
        }
        if (read.Lease is not { } lease)
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
    /// Takes the lease whoever holds it, and keeps it fresh until <see cref="Release"/>: <b>Take over</b>, and opening a
    /// session nobody else holds. The other machine finds out on its next refresh.
    /// </summary>
    public void TakeOver(string sessionId, string sessionFolder)
    {
        ThrowIfDisposed();
        Write(sessionFolder);
        lock (_lock)
        {
            _held[sessionId] = sessionFolder;
        }
    }

    /// <summary>
    /// Whether this run may write the session to the library: the lease is this run's, or nobody holds it (no file, or a
    /// stale lease this run never held). Writes nothing. When another run's lease has replaced one this run held, the
    /// session was taken over, and this raises <see cref="LeaseLost"/> as a refresh would. Leases are only refreshed once
    /// a minute, so a copy after a turn must ask here first, or it would overwrite what the other machine wrote and take
    /// the session back (DESIGN.md §9, "One machine at a time").
    /// </summary>
    public bool MayWrite(string sessionId, string sessionFolder) => Keep(sessionId, sessionFolder, write: false);

    /// <summary>
    /// Takes or refreshes the lease after writing the session, under the same rule as <see cref="MayWrite"/>: never from
    /// anyone else. Returns whether this run holds it.
    /// </summary>
    public bool Renew(string sessionId, string sessionFolder) => Keep(sessionId, sessionFolder, write: true);

    /// <remarks>
    /// The file is read and written outside the lock, which only guards the list of held sessions: a library folder on
    /// a slow or offline drive mustn't hold up every other lease, or the UI thread waiting on one.
    /// </remarks>
    private bool Keep(string sessionId, string sessionFolder, bool write)
    {
        ThrowIfDisposed();
        var read = Read(sessionFolder);
        if (read.Unreadable)
        {
            return false;
        }
        bool held;
        lock (_lock)
        {
            held = _held.ContainsKey(sessionId);
        }
        var free = read.Lease is not { } lease
            || lease.Owner == OwnerId
            || !held && _time.GetUtcNow() - lease.UpdatedAt >= StaleAfter;
        if (free)
        {
            if (write)
            {
                Write(sessionFolder);
                lock (_lock)
                {
                    _held[sessionId] = sessionFolder;
                }
            }
            return true;
        }
        bool lost;
        lock (_lock)
        {
            lost = _held.Remove(sessionId);
        }
        if (lost)
        {
            LeaseLost?.Invoke(sessionId, read.Lease!.Machine);
        }
        return false;
    }

    /// <summary>Stops refreshing the lease and deletes the file, but only if it's still ours.</summary>
    public void Release(string sessionId)
    {
        string? folder;
        lock (_lock)
        {
            _held.Remove(sessionId, out folder);
        }
        if (folder is not null)
        {
            DeleteIfMine(folder);
        }
    }

    /// <summary>Refreshes every held lease. Runs every <see cref="RefreshInterval"/>; public so the app can refresh sooner.</summary>
    public void RefreshAll()
    {
        KeyValuePair<string, string>[] held;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            held = [.. _held];
        }
        // Outside the lock: each file may take a while on a slow drive.
        List<(string SessionId, string Machine)>? lost = null;
        foreach (var (sessionId, folder) in held)
        {
            try
            {
                var read = Read(folder);
                if (read.Unreadable)
                {
                    // Perhaps another machine's take-over, half synced: don't write over it; look again next time.
                    continue;
                }
                if (read.Lease is { } lease && lease.Owner != OwnerId)
                {
                    lock (_lock)
                    {
                        // Released or taken over again meanwhile: nothing to report.
                        if (!_held.TryGetValue(sessionId, out var still) || still != folder)
                        {
                            continue;
                        }
                        _held.Remove(sessionId);
                    }
                    (lost ??= []).Add((sessionId, lease.Machine));
                    continue;
                }
                lock (_lock)
                {
                    if (_disposed || !_held.ContainsKey(sessionId))
                    {
                        // Released while this ran: don't bring its file back.
                        continue;
                    }
                }
                Write(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder may be briefly unavailable; try again next time.
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
        string[] folders;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            folders = [.. _held.Values];
            _held.Clear();
        }
        foreach (var folder in folders)
        {
            DeleteIfMine(folder);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>The lease in a session folder; no lease when there's no file, and <see cref="LeaseRead.Unreadable"/> when it can't be read.</summary>
    internal static LeaseRead Read(string sessionFolder)
    {
        try
        {
            var path = Path.Combine(sessionFolder, FileName);
            if (!File.Exists(path))
            {
                return LeaseRead.Missing;
            }
            if (JsonTree.Parse(LibraryFiles.ReadText(path)) is not JsonObject lease)
            {
                return LeaseRead.Failed;
            }
            var updatedAt = lease.GetString("updatedAt") is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            return new LeaseRead(new LeaseInfo(lease.GetString("machine") ?? "", updatedAt, lease.GetString("owner") ?? ""), false);
        }
        catch (FileNotFoundException)
        {
            return LeaseRead.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return LeaseRead.Missing;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return LeaseRead.Failed;
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
            if (Read(sessionFolder).Lease?.Owner == OwnerId)
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
