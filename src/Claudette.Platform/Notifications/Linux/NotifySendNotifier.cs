using System.Collections.Concurrent;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Notifications.Linux;

/// <summary>
/// Freedesktop notifications through <c>notify-send</c> (libnotify). Linux isn't a polished target (DESIGN.md §1), so
/// this stays simple: one <c>notify-send --wait</c> per notification, whose <c>default</c> action reports a click.
/// Versions before libnotify 0.7.10 don't know <c>--action</c>; they get plain notifications without clicks.
/// </summary>
public sealed class NotifySendNotifier : INotifier
{
    private readonly string _notifySend;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IRunningProcess> _waiting = new(StringComparer.Ordinal);
    private volatile bool _actionsUnsupported;

    public NotifySendNotifier(string notifySend, IProcessLauncher launcher, ILogger logger)
    {
        _notifySend = notifySend;
        _launcher = launcher;
        _logger = logger;
    }

    /// <summary>Null when <c>notify-send</c> isn't installed.</summary>
    public static NotifySendNotifier? TryCreate(IProcessLauncher launcher, ILogger logger, IFileProbe? probe = null) =>
        (probe ?? FileProbe.Instance).FindOnPath("notify-send") is { } path ? new NotifySendNotifier(path, launcher, logger) : null;

    public bool IsAvailable => true;

    public event Action<string>? Activated;

    public void Show(OsNotification notification)
    {
        Remove(notification.Id);
        IRunningProcess process;
        var withActions = !_actionsUnsupported;
        try
        {
            process = _launcher.Start(new ProcessStartSpec(_notifySend, Arguments(notification, withActions)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _logger.LogWarning(ex, "Couldn't run notify-send.");
            return;
        }
        process.CloseStandardInput();
        _waiting[notification.Id] = process;
        _ = WatchAsync(notification, process, withActions);
    }

    /// <summary><c>--</c> ends the options, so a title starting with a dash is still a title.</summary>
    public static IReadOnlyList<string> Arguments(OsNotification notification, bool withActions) => withActions
        ? ["--app-name=Claudette", "--action=default=Open", "--wait", "--", notification.Title, notification.Body]
        : ["--app-name=Claudette", "--", notification.Title, notification.Body];

    private async Task WatchAsync(OsNotification notification, IRunningProcess process, bool withActions)
    {
        var errors = new List<string>();
        var errorReader = Task.Run(async () =>
        {
            await foreach (var line in process.StandardError.ReadAllAsync().ConfigureAwait(false))
            {
                errors.Add(line);
            }
        });
        try
        {
            await foreach (var line in process.StandardOutput.ReadAllAsync().ConfigureAwait(false))
            {
                // With --wait, notify-send prints the invoked action's key.
                if (line.Trim() == "default")
                {
                    Activated?.Invoke(notification.Id);
                }
            }
            var exitCode = await process.Exited.ConfigureAwait(false);
            await errorReader.ConfigureAwait(false);
            if (exitCode != 0 && withActions && errors.Any(e => e.Contains("Unknown option", StringComparison.OrdinalIgnoreCase)))
            {
                // An older libnotify: fall back to notifications without a click action.
                _actionsUnsupported = true;
                _waiting.TryRemove(new KeyValuePair<string, IRunningProcess>(notification.Id, process));
                Show(notification);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "notify-send ended unexpectedly.");
        }
        finally
        {
            _waiting.TryRemove(new KeyValuePair<string, IRunningProcess>(notification.Id, process));
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>notify-send can't close a notification it showed; this only stops waiting for its click.</summary>
    public void Remove(string id)
    {
        if (_waiting.TryRemove(id, out var process))
        {
            process.Kill();
        }
    }

    public void Dispose()
    {
        foreach (var id in _waiting.Keys.ToArray())
        {
            Remove(id);
        }
    }
}
