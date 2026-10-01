using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Claudette.Core.Logging;

/// <summary>
/// Claudette's own log (DESIGN.md §13, "Diagnostics"): warnings and errors, one line each with the exception after it,
/// in <c>claudette.log</c> in the log folder. It's kept under <see cref="MaxBytes"/>: when it grows past that, it
/// becomes <c>claudette.1.log</c>, replacing the one before. Never throws: a log that can't be written is skipped.
/// </summary>
public sealed class FileLoggerFactory(string directory, TimeProvider time, LogLevel minimum = LogLevel.Warning) : ILoggerFactory
{
    public const string FileName = "claudette.log";
    public const string PreviousFileName = "claudette.1.log";
    public const long MaxBytes = 2 * 1024 * 1024;

    private readonly Lock _lock = new();

    public string Path => System.IO.Path.Combine(directory, FileName);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Only this file: other providers aren't added.</summary>
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("Claudette's log has one provider.");

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(time.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(Level(level))
            .Append(' ').Append(category)
            .Append(": ").Append(message.ReplaceLineEndings(" "))
            .Append('\n');
        if (exception is not null)
        {
            line.Append("    ").Append(exception.ToString().ReplaceLineEndings("\n    ")).Append('\n');
        }
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var file = new FileInfo(Path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(Path, System.IO.Path.Combine(directory, PreviousFileName), overwrite: true);
                }
                File.AppendAllText(Path, line.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to say so; the next line tries again.
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "?    ",
    };

    private sealed class FileLogger(FileLoggerFactory provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }

    private readonly LogLevel _minimum = minimum;
}
