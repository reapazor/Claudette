using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.Settings;

/// <summary>
/// Loads and saves one JSON file. Writes go to a temporary file first and are then renamed into place, so a crash
/// never leaves a half-written file. A file that can't be read is backed up and replaced with defaults.
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

    public string Path { get; } = path;

    public T Load()
    {
        if (!File.Exists(Path))
        {
            return new T();
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(Path), Options) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            var backup = $"{Path}.{DateTime.UtcNow:yyyyMMddHHmmss}.bad";
            _logger.LogWarning(ex, "Couldn't read {Path}; keeping it as {Backup} and starting from defaults.", Path, backup);
            try
            {
                File.Move(Path, backup, overwrite: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
            return new T();
        }
    }

    public Task SaveAsync(T value, CancellationToken cancellationToken = default) =>
        WriteAsync(Serialize(value), cancellationToken);

    /// <summary>Serialize on the thread that owns <paramref name="value"/>; then <see cref="WriteAsync"/> anywhere.</summary>
    public static string Serialize(T value) => JsonSerializer.Serialize(value, Options);

    public async Task WriteAsync(string json, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
