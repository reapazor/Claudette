using System.Text.Json;
using System.Text.Json.Serialization;
using Claudette.Core.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.Settings;

/// <summary>
/// Loads and saves one JSON file. Writes go to a temporary file first, flushed to the disk and then renamed into place,
/// so a crash or a power cut never leaves a half-written file. A file that isn't valid JSON is set aside and replaced
/// with defaults. A file that can't be read at all (in use for longer than the retries wait, or not permitted) is left
/// alone: this run starts from defaults but doesn't save over it, so it comes back at the next launch.
/// </summary>
public sealed class JsonFileStore<T>(string path, ILogger? logger = null) where T : class, new()
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _couldNotRead;

    public string Path { get; } = path;

    /// <summary>The last <see cref="Load"/> couldn't read the file, so saves leave it as it is until a load succeeds.</summary>
    public bool CouldNotRead => _couldNotRead;

    public T Load()
    {
        string json;
        try
        {
            if (!File.Exists(Path))
            {
                _couldNotRead = false;
                return new T();
            }
            json = AtomicFile.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _couldNotRead = false;
            return new T();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not damaged, just not readable now: keep it for the next launch rather than saving defaults over it.
            _couldNotRead = true;
            _logger.LogWarning(ex, "Couldn't read {Path}; starting from defaults this time, without saving over it.", Path);
            return new T();
        }
        _couldNotRead = false;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            SetAside(ex);
            return new T();
        }
    }

    public Task SaveAsync(T value, CancellationToken cancellationToken = default) =>
        WriteAsync(Serialize(value), cancellationToken);

    /// <summary>Serialize on the thread that owns <paramref name="value"/>; then <see cref="WriteAsync"/> anywhere.</summary>
    public static string Serialize(T value) => JsonSerializer.Serialize(value, Options);

    public async Task WriteAsync(string json, CancellationToken cancellationToken = default)
    {
        if (_couldNotRead)
        {
            _logger.LogDebug("Not saving {Path}: it couldn't be read at launch, and is kept as it was.", Path);
            return;
        }
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteAllTextAsync(Path, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Keeps a file that isn't valid JSON next to it, named for when it was last written.</summary>
    private void SetAside(Exception ex)
    {
        string backup;
        try
        {
            backup = $"{Path}.{File.GetLastWriteTimeUtc(Path):yyyyMMddHHmmss}.bad";
        }
        catch (Exception timeError) when (timeError is IOException or UnauthorizedAccessException)
        {
            backup = $"{Path}.{Guid.NewGuid():N}.bad";
        }
        _logger.LogWarning(ex, "Couldn't read {Path}; keeping it as {Backup} and starting from defaults.", Path, backup);
        try
        {
            FileRetry.Run(() => File.Move(Path, backup, overwrite: true));
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
