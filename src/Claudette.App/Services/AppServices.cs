using Claudette.Core;
using Claudette.Core.Auth;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.App.Services;

/// <summary>
/// The app's composition root. Holds the shared services, and the ones that exist once Claude Code has been found.
/// </summary>
public sealed class AppServices(
    AppPaths paths,
    IProcessLauncher launcher,
    TimeProvider timeProvider,
    IPlatformServices platform,
    IUiDispatcher dispatcher,
    ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    private UtilitySession? _utility;

    public AppPaths Paths { get; } = paths;

    public TimeProvider Time { get; } = timeProvider;

    public IPlatformServices Platform { get; } = platform;

    public IUiDispatcher Dispatcher { get; } = dispatcher;

    public ILoggerFactory Loggers { get; } = loggerFactory ?? NullLoggerFactory.Instance;

    public ClaudeLocator Locator { get; } = new(launcher, timeProvider);

    public ClaudeInstall? Install { get; private set; }

    public IClaudeSessionFactory? Sessions { get; private set; }

    public ClaudeAuth? Auth { get; private set; }

    public void UseInstall(ClaudeInstall install)
    {
        Install = install;
        Sessions = new ClaudeSessionFactory(install.Path, launcher, Time, Loggers);
        Auth = new ClaudeAuth(install.Path, launcher, Time);
    }

    /// <summary>The hidden utility session, started on first use and restarted if it has exited.</summary>
    public async Task<UtilitySession> GetUtilitySessionAsync(CancellationToken cancellationToken = default)
    {
        if (_utility is { Completion.IsCompleted: false })
        {
            return _utility;
        }
        if (_utility is not null)
        {
            await _utility.DisposeAsync();
        }
        var factory = Sessions ?? throw new InvalidOperationException("Claude Code hasn't been found yet.");
        _utility = await UtilitySession.StartAsync(factory, Paths.UtilityDirectory, cancellationToken);
        return _utility;
    }

    public async ValueTask DisposeAsync()
    {
        if (_utility is not null)
        {
            await _utility.DisposeAsync();
            _utility = null;
        }
    }
}
