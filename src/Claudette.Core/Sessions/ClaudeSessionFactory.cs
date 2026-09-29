using Claudette.Core.Claude;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.Sessions;

public interface IClaudeSessionFactory
{
    /// <summary>Starts a <c>claude</c> process and completes the <c>initialize</c> handshake.</summary>
    Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default);
}

/// <param name="environment">The user environment each <c>claude</c> starts from (DESIGN.md §13). Null: Claudette's own.</param>
public sealed class ClaudeSessionFactory(
    string claudePath,
    IProcessLauncher launcher,
    TimeProvider timeProvider,
    ILoggerFactory? loggerFactory = null,
    ProtocolDiagnostics? diagnostics = null,
    UserEnvironment? environment = null) : IClaudeSessionFactory
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public async Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default)
    {
        var userEnvironment = environment is null ? null : await environment.GetAsync(cancellationToken).ConfigureAwait(false);
        var spec = new ProcessStartSpec(claudePath, ClaudeArguments.ForStreamingSession(options))
        {
            WorkingDirectory = options.WorkingDirectory,
            Environment = ClaudeEnvironment.From(userEnvironment, options.EnvironmentOverrides),
            TrackProcessTree = true,
        };
        IClaudeTransport transport = new ProcessClaudeTransport(launcher.Start(spec), _loggerFactory.CreateLogger<ProcessClaudeTransport>());
        if (options.ProtocolLogPath is { } logPath)
        {
            transport = new LoggingTransport(transport, new ProtocolLog(logPath, timeProvider));
        }
        var session = new ClaudeSession(transport, timeProvider, _loggerFactory.CreateLogger<ClaudeSession>(), diagnostics);
        try
        {
            await session.InitializeAsync(options.Hooks, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
