using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Claudette.Core.Protocol;

/// <summary>
/// A session's raw protocol traffic, one line per message with its time and direction (DESIGN.md §13, "Logging"):
/// <c>&lt;</c> from Claude Code, <c>&gt;</c> to it. Off by default (Settings → Advanced), because it holds everything
/// the session sees, file contents included.
/// </summary>
public sealed class ProtocolLog : IDisposable
{
    /// <summary>Logs older than this are deleted when Claudette starts.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private readonly StreamWriter _writer;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <param name="path">
    /// Where to write. When another log already has the name (two tabs in the same folder starting in the same second),
    /// this one gets <c>-2</c>, <c>-3</c> and so on before the extension; <see cref="Path"/> says which.
    /// </param>
    public ProtocolLog(string path, TimeProvider time)
    {
        _time = time;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1
                ? path
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{System.IO.Path.GetFileNameWithoutExtension(path)}-{attempt}{System.IO.Path.GetExtension(path)}");
            try
            {
                _writer = new StreamWriter(new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
                Path = candidate;
                return;
            }
            catch (IOException) when (attempt < 100 && File.Exists(candidate))
            {
                // Taken: try the next name.
            }
        }
    }

    public string Path { get; }

    /// <summary>A file name for a new log: when it started and a label, such as the tab's folder name.</summary>
    public static string FileName(DateTimeOffset now, string label)
    {
        var safe = new string(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return $"{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{(safe.Length > 0 ? safe : "session")}.log";
    }

    public void Received(string line) => Write('<', line);

    public void Sent(string line) => Write('>', line);

    private void Write(char direction, string line)
    {
        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                _writer.Write(stamp);
                _writer.Write(' ');
                _writer.Write(direction);
                _writer.Write(' ');
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // A full disk mustn't end the session.
            }
        }
    }

    /// <summary>Deletes logs in <paramref name="folder"/> older than <see cref="KeepFor"/>.</summary>
    public static void DeleteOld(string folder, DateTimeOffset now)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.log"))
        {
            try
            {
                if (now - file.LastWriteTimeUtc > KeepFor)
                {
                    file.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use; next time.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _writer.Dispose();
        }
    }
}

/// <summary>A transport that writes everything passing through it to a <see cref="ProtocolLog"/>.</summary>
public sealed class LoggingTransport : IClaudeTransport
{
    private readonly IClaudeTransport _inner;
    private readonly ProtocolLog _log;
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });

    public LoggingTransport(IClaudeTransport inner, ProtocolLog log)
    {
        _inner = inner;
        _log = log;
        _ = PumpAsync();
    }

    public ChannelReader<string> Output => _output.Reader;

    public Task<TransportExit> Completion => _inner.Completion;

    public int? ProcessId => _inner.ProcessId;

    public ValueTask SendAsync(string line, CancellationToken cancellationToken = default)
    {
        _log.Sent(line);
        return _inner.SendAsync(line, cancellationToken);
    }

    public void CloseInput() => _inner.CloseInput();

    public void Terminate() => _inner.Terminate();

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var line in _inner.Output.ReadAllAsync().ConfigureAwait(false))
            {
                _log.Received(line);
                _output.Writer.TryWrite(line);
            }
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        _log.Dispose();
    }
}
