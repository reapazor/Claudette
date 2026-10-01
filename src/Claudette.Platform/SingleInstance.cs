using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform;

/// <summary>
/// One Claudette per user and data folder. A second launch, for example from the Windows jump list or macOS's Open
/// Recent with <c>--folder &lt;path&gt;</c> (DESIGN.md §4, "Other ways in"), passes its arguments to the running one
/// over a named pipe (a Unix domain socket on macOS and Linux) and exits.
/// </summary>
/// <remarks>
/// Which launch is the running one is settled by a lock on <see cref="ClaimFileName"/> in the data folder, so two
/// launches at the same moment can't both start. The operating system lets go of the lock when the process ends, so a
/// Claudette that crashed never leaves it held.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The file in the data folder whose lock says which Claudette is running.</summary>
    public const string ClaimFileName = "instance.lock";

    /// <summary>How long a launch waits for the Claudette that holds the claim to take its arguments.</summary>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LongestClaimRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestListenRetry = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly string _claimPath;
    private readonly TimeProvider _time;
    private readonly TimeSpan _patience;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private FileStream? _claim;
    private CancellationTokenSource? _stop;
    private Task? _listening;

    /// <param name="scope">
    /// What makes an instance separate: the data folder, which holds the claim. A development copy run with
    /// <c>CLAUDETTE_HOME</c> is separate from the installed one.
    /// </param>
    /// <param name="patience">How long <see cref="StartAsync"/> waits for another launch that holds the claim.</param>
    public SingleInstance(string scope, ILogger? logger = null, TimeProvider? timeProvider = null, TimeSpan? patience = null)
    {
        _pipeName = PipeName(scope);
        _claimPath = Path.Combine(Path.GetFullPath(scope), ClaimFileName);
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _patience = patience ?? DefaultPatience;
    }

    /// <summary>Raised on a background thread with the arguments of each later launch.</summary>
    public event Action<IReadOnlyList<string>>? ArgumentsReceived;

    /// <summary>Whether this Claudette holds the claim on its data folder.</summary>
    public bool IsClaimed
    {
        get
        {
            lock (_lock)
            {
                return _claim is not null;
            }
        }
    }

    /// <summary>
    /// Hands <paramref name="args"/> to a Claudette that's already running. Returns true if one took them, in which case
    /// this launch should exit; false if none is listening.
    /// </summary>
    public async Task<bool> TryHandOffAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        try
        {
            // CurrentUserOnly: only hand arguments to a Claudette run by this user.
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(args)).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Nobody is listening, or it isn't this user's.
            return false;
        }
    }

    /// <summary>
    /// Makes this launch the running Claudette and starts taking later launches, or hands <paramref name="args"/> to the
    /// launch that got there first. Returns true if this launch is the running one; false if another took the
    /// arguments, in which case this launch should exit.
    /// </summary>
    /// <param name="takeOver">
    /// This is the build or version an earlier Claudette restarted into (DESIGN.md §9), which has let go of the claim:
    /// wait for it, and never hand the arguments to anyone.
    /// </param>
    public async Task<bool> StartAsync(IReadOnlyList<string> args, bool takeOver = false, CancellationToken cancellationToken = default)
    {
        var started = _time.GetTimestamp();
        var wait = FirstRetry;
        while (true)
        {
            if (TryClaim())
            {
                Listen();
                return true;
            }
            // The launch that holds the claim may not be listening yet.
            if (!takeOver && await TryHandOffAsync(args, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            if (_time.GetElapsedTime(started) >= _patience)
            {
                // Held by a Claudette that never took the arguments. Starting is better than doing nothing.
                _logger.LogWarning("Another Claudette holds {Path} but didn't take this launch; starting anyway.", _claimPath);
                Listen();
                return true;
            }
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestClaimRetry.Ticks));
        }
    }

    /// <summary>
    /// Starts taking arguments from later launches, claiming the data folder if no other Claudette holds it. Also how a
    /// Claudette takes launches back after a restart that didn't happen.
    /// </summary>
    public void Listen()
    {
        lock (_lock)
        {
            if (_listening is not null)
            {
                return;
            }
            if (!TryClaimLocked())
            {
                _logger.LogWarning("Another Claudette holds {Path}; taking launches without it.", _claimPath);
            }
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _listening = Task.Run(() => ListenAsync(token));
        }
    }

    /// <summary>
    /// Stops taking later launches and lets go of the claim, so another Claudette can have both: the new build or
    /// version Claudette restarts into (DESIGN.md §9). <see cref="Listen"/> takes them again.
    /// </summary>
    public void StopListening()
    {
        Task? listening;
        CancellationTokenSource? stop;
        lock (_lock)
        {
            stop = _stop;
            stop?.Cancel();
            listening = _listening;
            _stop = null;
            _listening = null;
        }
        try
        {
            listening?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        stop?.Dispose();
        // Let go only once the pipe is closed, so whoever claims next can listen.
        lock (_lock)
        {
            if (_listening is null)
            {
                _claim?.Dispose();
                _claim = null;
            }
        }
    }

    private bool TryClaim()
    {
        lock (_lock)
        {
            return TryClaimLocked();
        }
    }

    private bool TryClaimLocked()
    {
        if (_claim is not null)
        {
            return true;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_claimPath)!);
            // FileShare.None is a lock on every OS: an exclusive flock on macOS and Linux. The file stays, empty: deleting
            // it could let two launches lock different files of the same name.
            _claim = new FileStream(_claimPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Couldn't claim {Path}.", _claimPath);
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken stop)
    {
        NamedPipeServerStream? server = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                server ??= await CreateServerAsync(stop).ConfigureAwait(false);
                try
                {
                    await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A launch that connected and went at once can break the pipe: open another and keep listening, or
                    // every later launch would start a second Claudette.
                    _logger.LogDebug(ex, "Waiting for a later launch failed; listening again.");
                    await server.DisposeAsync().ConfigureAwait(false);
                    server = null;
                    continue;
                }
                // The next one before this one closes: launches at the same moment wait in its queue. On macOS and
                // Linux, closing the last one in the process closes the socket, and with it any launch still waiting.
                var connected = server;
                server = TryCreateServer(out _);
                await ReadAsync(connected, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens the pipe, trying again less and less often while another user's pipe of the name is in the way.</summary>
    private async Task<NamedPipeServerStream> CreateServerAsync(CancellationToken stop)
    {
        var wait = FirstRetry;
        for (var failures = 0; ; failures++)
        {
            if (TryCreateServer(out var error) is { } server)
            {
                return server;
            }
            if (failures == 0)
            {
                _logger.LogWarning(error, "Couldn't listen for later launches; trying again.");
            }
            await Task.Delay(wait, _time, stop).ConfigureAwait(false);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestListenRetry.Ticks));
        }
    }

    private NamedPipeServerStream? TryCreateServer(out Exception? error)
    {
        try
        {
            error = null;
            return new NamedPipeServerStream(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            return null;
        }
    }

    private async Task ReadAsync(NamedPipeServerStream connected, CancellationToken stop)
    {
        await using (connected.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(connected, Encoding.UTF8);
                if (await reader.ReadLineAsync(stop).ConfigureAwait(false) is { } line
                    && JsonSerializer.Deserialize<string[]>(line) is { } args)
                {
                    try
                    {
                        ArgumentsReceived?.Invoke(args);
                    }
                    catch (Exception ex)
                    {
                        // The app's handler failed on these: later launches still reach it.
                        _logger.LogWarning(ex, "A later launch's arguments couldn't be handled.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // One launch's arguments: the next can still be read.
                _logger.LogDebug(ex, "A later launch's arguments couldn't be read.");
            }
        }
    }

    /// <summary>
    /// A name unique to this user and scope, short enough for a Unix socket path. <c>CurrentUserOnly</c> keeps other
    /// users out as well.
    /// </summary>
    public static string PipeName(string scope)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}|{Path.GetFullPath(scope)}"));
        return $"claudette-{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
    }

    public void Dispose() => StopListening();
}
