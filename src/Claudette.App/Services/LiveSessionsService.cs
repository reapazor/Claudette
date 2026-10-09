using Claudette.Core.ProjectTools;
using Claudette.Core.Sessions;

namespace Claudette.App.Services;

/// <summary>
/// The Claude Code sessions running on this machine (DESIGN.md §13, "Session naming"), from Claude Code's sessions
/// folder: the name each tab's session is reached by, for its info card, and the other sessions the composer's
/// <c>@</c> offers (DESIGN.md §5, "Autocomplete"). Read off the UI thread when a tab's session starts and whenever
/// Claude Code writes to the folder, which it does as a session starts, ends, is renamed, or starts or ends a turn.
/// Claudette's own utility session isn't one of them.
/// </summary>
public sealed class LiveSessionsService(AppServices services, ISystemProcesses? processes) : IDisposable
{
    private readonly Lock _lock = new();
    private bool _reading;
    private bool _readAgain;
    private FileSystemWatcher? _watcher;
    private string? _watched;
    private bool _disposed;

    /// <summary>The sessions found by the last read, by name; none before the first.</summary>
    public IReadOnlyList<LiveSession> Current { get; private set; } = [];

    /// <summary>Raised on the UI thread when a read finds anything different.</summary>
    public event Action? Changed;

    /// <summary>
    /// The session in this process or, when <c>claude</c> was started through another program, with this ID; null if it
    /// has no entry. The process comes first: a copy of a session starts with its original's ID.
    /// </summary>
    public LiveSession? Find(string? sessionId, int? pid) =>
        (pid is { } id ? Current.FirstOrDefault(s => s.Pid == id) : null) ?? (sessionId is null ? null : Current.FirstOrDefault(s => s.SessionId == sessionId));

    /// <summary>Reads the folder again in the background. A read asked for during one runs after it.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            if (_reading)
            {
                _readAgain = true;
                return;
            }
            _reading = true;
        }
        _ = Task.Run(Read);
    }

    private void Read()
    {
        while (true)
        {
            var configDirectory = services.ClaudeConfigDirectory;
            Watch(configDirectory);
            var utility = Path.TrimEndingDirectorySeparator(services.Paths.UtilityDirectory);
            var found = LiveSessions.Read(configDirectory, pid => processes?.IsRunning(pid) ?? true)
                .Where(s => s.Cwd is not { } cwd || !string.Equals(Path.TrimEndingDirectorySeparator(cwd), utility, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            services.Dispatcher.Post(() =>
            {
                if (!found.SequenceEqual(Current))
                {
                    Current = found;
                    Changed?.Invoke();
                }
            });
            lock (_lock)
            {
                if (!_readAgain)
                {
                    _reading = false;
                    return;
                }
                _readAgain = false;
            }
        }
    }

    /// <summary>
    /// Watches the sessions folder once Claude Code has made it. Claude Code writes a rename there just after it answers
    /// <c>rename_session</c>, so a read straight after the answer can come too soon (checked against 2.1.284).
    /// </summary>
    private void Watch(string? configDirectory)
    {
        var folder = configDirectory is null ? null : LiveSessions.Folder(configDirectory);
        lock (_lock)
        {
            if (_disposed || folder is null || folder == _watched && _watcher is not null || !Directory.Exists(folder))
            {
                return;
            }
            _watcher?.Dispose();
            _watcher = null;
            _watched = folder;
            try
            {
                var watcher = new FileSystemWatcher(folder, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                watcher.Created += OnFolderChanged;
                watcher.Changed += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                // It's read when tabs start and mentions begin instead.
            }
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) => Refresh();

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
