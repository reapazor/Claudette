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
public sealed class SingleInstance : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _stop;
    private Task? _listening;

    /// <param name="scope">
    /// What makes an instance separate, normally the data folder: a development copy run with <c>CLAUDETTE_HOME</c> is
    /// separate from the installed one.
    /// </param>
    public SingleInstance(string scope, ILogger? logger = null)
    {
        _pipeName = PipeName(scope);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised on a background thread with the arguments of each later launch.</summary>
    public event Action<IReadOnlyList<string>>? ArgumentsReceived;

    /// <summary>
    /// Hands <paramref name="args"/> to a Claudette that's already running. Returns true if one took them, in which case
    /// this launch should exit; false if this is the first instance, which should then call <see cref="Listen"/>.
    /// </summary>
    public async Task<bool> TryHandOffAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(args)).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Nobody is listening: this is the first instance.
            return false;
        }
    }

    /// <summary>Starts taking arguments from later launches.</summary>
    public void Listen()
    {
        lock (_lock)
        {
            if (_listening is not null)
            {
                return;
            }
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _listening = Task.Run(() => ListenAsync(token));
        }
    }

    /// <summary>
    /// Stops taking later launches, so another Claudette can: the new build Claudette restarts into (DESIGN.md §9).
    /// <see cref="Listen"/> starts again.
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
    }

    private async Task ListenAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                if (await reader.ReadLineAsync(stop).ConfigureAwait(false) is { } line
                    && JsonSerializer.Deserialize<string[]>(line) is { } args)
                {
                    ArgumentsReceived?.Invoke(args);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "A later launch's arguments couldn't be read.");
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
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
